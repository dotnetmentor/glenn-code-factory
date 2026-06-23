// ClaudeFactory — the thin seam between TurnRunner and
// `@anthropic-ai/claude-agent-sdk`.
//
// Structurally parallel to `buildCursorFactory`: build an `AgentFactory`
// closure bound to per-daemon dependencies (MCP registry, runtime token
// getter, project repo dir, git branch resolver, default model); the closure
// is invoked per turn by `TurnRunner` and yields the same wire `TurnEvent`
// frames the Cursor path yields — so the chat panel and SignalR contract need
// no Claude-specific changes (Phase 1 of the claude-agent-backend spec).
//
// === Turn lifecycle ===
//
// The Claude Agent SDK exposes `query({ prompt, options }) → Query`, an
// async-generator of `SDKMessage`s with `.interrupt()` / `.setModel()`
// control methods. We translate this into the daemon's "one stream per turn"
// model:
//
//   1. Build `options`: model (alias-mapped + default fallback), cwd, resume
//      (opts.resume = the SDK session_id from a previous turn's init frame),
//      mcpServers, permissionMode (yolo → bypassPermissions else acceptEdits),
//      effort (reasoning), thinking:{type:'adaptive'} on reasoning-capable
//      models, systemPrompt preamble on the FIRST turn.
//   2. Call `query({ prompt, options })`.
//   3. Iterate the Query and translate each `SDKMessage` via `mapClaudeMessage`.
//   4. On abort: pass an AbortController to the SDK (`options.abortController`)
//      and abort it; also best-effort `.interrupt()`.
//   5. On the terminal `result` message, stage the RunResultPayload aggregate
//      so the mapper attaches it to the terminal Status frame.
//
// === System prompt handling (parity with Cursor) ===
//
// We assemble the platform harness + project rules (`.claude/rules`,
// `.cursor/rules`) into a preamble and set it as `options.systemPrompt` on the
// FIRST turn (no resume hint). On resume turns the SDK session already carries
// the conversation context, so we omit the preamble to avoid token waste.
//
// === BYOK handoff ===
//
// The SDK reads `process.env.ANTHROPIC_API_KEY`. We scope an env override
// around the query call (snapshot → set → restore in finally), mirroring
// CursorFactory's CURSOR_API_KEY handoff. The key comes from the new
// `secrets.anthropicApiKey` field.
//
// === Resume resilience (parity with Cursor) ===
//
// `resume: sessionId` re-attaches to a prior SDK session. If the SDK throws
// "session not found" (ephemeral runtime respawn wiped the local session
// files), we fall back to a fresh `query()` WITH the harness preamble — the
// platform's own AgentEvents history is the source of truth, only the SDK's
// local context is lost.
//
// === Lazy SDK import ===
//
// Per-call dynamic `import('@anthropic-ai/claude-agent-sdk')`, same as
// CursorFactory's `import('@cursor/sdk')`, so quiet-mode can drop the module
// reference. A narrow structural `ClaudeSdkModule` keeps us version-tolerant
// and lets the test seam inject a stub.

import type { Logger } from 'pino'
import { readdir, readFile } from 'node:fs/promises'
import path from 'node:path'

import type { TurnEvent } from './TurnEvent.js'
import type { AgentFactory } from './AgentFactory.js'
import type { TurnOptions } from './TurnOptions.js'
import type { McpRegistry } from '../mcp/McpRegistry.js'
import { getHarness } from '../harness/index.js'
import {
  makeClaudeMapperState,
  mapClaudeMessage,
  noteClaudeTerminalRunResult,
  noteClaudeUsage,
  synthesizeClaudeTerminalStatus,
  type ClaudeSdkMessage,
  type ClaudeMapperState,
} from './ClaudeEventMapper.js'
import { AgentEventRunStatus } from '../signalr/types.js'
import type { RunResultPayload } from '../generated/signalr/Source.Features.SignalR.Contracts.js'

// ---------------------------------------------------------------------------
// Model alias + reasoning-capability tables (authoritative — spec §5)
// ---------------------------------------------------------------------------

/** Friendly alias → canonical model id. Spec §5. */
const MODEL_ALIASES: Readonly<Record<string, string>> = {
  opus: 'claude-opus-4-8',
  sonnet: 'claude-sonnet-4-6',
  haiku: 'claude-haiku-4-5',
  fable: 'claude-fable-5',
}

/**
 * Resolve a model id/alias to a canonical id. Pass-through for anything not in
 * the alias table (already-canonical ids, future models).
 */
export function resolveClaudeModel(modelOrAlias: string): string {
  return MODEL_ALIASES[modelOrAlias] ?? modelOrAlias
}

/**
 * True when the model supports adaptive thinking (Opus 4.7/4.8 and Fable 5 per
 * spec §5). We send `thinking: { type: 'adaptive' }` ONLY for these — sending
 * a budget_tokens thinking config 400s on these models, and sending adaptive
 * to a non-reasoning model is rejected. Conservative substring match against
 * the canonical id so a freshly-aliased Opus/Fable point release is covered.
 */
export function modelSupportsReasoning(canonicalModelId: string): boolean {
  const id = canonicalModelId.toLowerCase()
  if (id.includes('opus-4-7') || id.includes('opus-4-8')) return true
  if (id.includes('fable-5')) return true
  return false
}

// ---------------------------------------------------------------------------
// Narrow structural SDK shapes (tolerant — see header)
// ---------------------------------------------------------------------------

/**
 * The subset of the SDK's `Options` we set. Kept narrow so a future SDK minor
 * doesn't break us and the test seam can validate the exact options we build.
 */
export interface ClaudeQueryOptions {
  model?: string
  cwd?: string
  resume?: string
  mcpServers?: Record<string, unknown>
  permissionMode?: 'default' | 'acceptEdits' | 'bypassPermissions' | 'plan' | 'dontAsk' | 'auto'
  effort?: 'low' | 'medium' | 'high' | 'xhigh' | 'max'
  thinking?: { type: 'adaptive' } | { type: 'enabled'; budgetTokens?: number } | { type: 'disabled' }
  systemPrompt?: string
  abortController?: AbortController
  /** SDK reads ANTHROPIC_API_KEY from env; we also forward via env per-turn. */
  env?: Record<string, string | undefined>
}

/** The subset of the SDK's `Query` async-generator we consume. */
export interface ClaudeSdkQuery extends AsyncIterable<ClaudeSdkMessage> {
  interrupt?(): Promise<void>
  setModel?(model?: string): Promise<void>
}

/** Minimal module shape from `@anthropic-ai/claude-agent-sdk`. */
export interface ClaudeSdkModule {
  query(params: { prompt: string; options?: ClaudeQueryOptions }): ClaudeSdkQuery
}

export interface BuildClaudeFactoryDeps {
  logger: Logger
  /** Project-scoped MCP snapshot. Read every turn (parity with Cursor). */
  mcpRegistry?: Pick<McpRegistry, 'entries'>
  /** Runtime token resolver for the MCP `Authorization: Bearer` header. */
  getRuntimeToken?: () => string
  /** Git branch resolver for the `X-Daemon-Git-Branch` MCP header. */
  getGitBranch?: () => Promise<string | null>
  /** Absolute path to the project repo, for reading rule files. */
  projectRepoDir?: string
  /**
   * Default model id when `opts.model` is undefined. Production passes the
   * configured `CLAUDE_DEFAULT_MODEL` (default `claude-opus-4-8`). Unlike
   * Cursor the SDK has a CLI default, but the daemon pins one so behavior is
   * deterministic per-project.
   */
  defaultModel?: string
  /** Test seam — replace the SDK import with a stub. */
  importSdk?: () => Promise<ClaudeSdkModule>
}

/**
 * Build the Claude `AgentFactory`. Injected into TurnRunner's factory map
 * alongside `buildCursorFactory`; selected per-turn by the `backend`
 * discriminator (default `cursor`).
 */
export function buildClaudeFactory(deps: BuildClaudeFactoryDeps): AgentFactory {
  const logger = deps.logger.child({ module: 'claude-factory' })
  const projectRepoDir = deps.projectRepoDir
  const defaultModel = deps.defaultModel
  const importSdk: () => Promise<ClaudeSdkModule> =
    deps.importSdk ??
    (async () =>
      (await import('@anthropic-ai/claude-agent-sdk')) as unknown as ClaudeSdkModule)

  return (opts: TurnOptions) => ({
    [Symbol.asyncIterator]: async function* (): AsyncGenerator<TurnEvent, void, void> {
      // ------------------------------------------------------------------
      // 1) BYOK env handoff — ANTHROPIC_API_KEY
      // ------------------------------------------------------------------
      const anthropicApiKey = opts.secrets?.anthropicApiKey ?? null
      const priorEnvValue = process.env['ANTHROPIC_API_KEY']
      const restoreEnv = (): void => {
        if (priorEnvValue === undefined) {
          delete process.env['ANTHROPIC_API_KEY']
        } else {
          process.env['ANTHROPIC_API_KEY'] = priorEnvValue
        }
      }
      if (anthropicApiKey !== null && anthropicApiKey !== '') {
        process.env['ANTHROPIC_API_KEY'] = anthropicApiKey
      } else {
        // Explicitly absent — let the SDK surface a clean auth error rather
        // than picking up a stale daemon-env value.
        delete process.env['ANTHROPIC_API_KEY']
      }

      // ------------------------------------------------------------------
      // 2) Resolve model (alias-map + default fallback)
      // ------------------------------------------------------------------
      const modelOpt = opts.model
      let model: string
      if (typeof modelOpt === 'string' && modelOpt !== '') {
        model = resolveClaudeModel(modelOpt)
      } else if (defaultModel !== undefined && defaultModel !== '') {
        model = resolveClaudeModel(defaultModel)
      } else {
        restoreEnv()
        throw new Error(
          'ClaudeFactory: no model resolved — neither opts.model nor defaultModel was set',
        )
      }

      // ------------------------------------------------------------------
      // 3) Build MCP servers map (parity with Cursor)
      // ------------------------------------------------------------------
      // The Claude SDK's `mcpServers` is `Record<name, McpServerConfig>` where
      // an HTTP entry is `{ type: 'http', url, headers }` — the SAME shape the
      // daemon's registry + TurnRunner already produce.
      const mcpEntries = deps.mcpRegistry?.entries() ?? []
      const mcpServers: Record<string, unknown> = {}
      if (mcpEntries.length > 0) {
        const branch =
          deps.getGitBranch !== undefined ? await deps.getGitBranch() : null
        const buildHeaders = (): Record<string, string> => {
          const h: Record<string, string> = {}
          if (deps.getRuntimeToken !== undefined) {
            h.Authorization = `Bearer ${deps.getRuntimeToken()}`
          }
          if (branch !== null && branch !== '') {
            h['X-Daemon-Git-Branch'] = branch
          }
          return h
        }
        for (const entry of mcpEntries) {
          mcpServers[entry.name] = {
            type: 'http',
            url: entry.baseUrl,
            headers: buildHeaders(),
          }
        }
      }
      // Overlay per-turn MCP servers TurnRunner passed in — only declarative
      // transport shapes the SDK can wire over its bridge: http/sse (url) or
      // stdio (command). In-process Server INSTANCES (e.g. daemon-tools) are
      // dropped with a warn, same defensive filter as the Cursor path.
      if (opts.mcpServers !== undefined) {
        for (const [k, v] of Object.entries(opts.mcpServers)) {
          if (v === null || typeof v !== 'object') {
            logger.warn(
              { name: k, valueType: typeof v },
              '[claude] dropping non-object mcpServers entry',
            )
            continue
          }
          const obj = v as Record<string, unknown>
          const type = typeof obj.type === 'string' ? (obj.type as string) : undefined
          const isHttpLike =
            (type === 'http' || type === 'sse' || type === undefined) &&
            typeof obj.url === 'string'
          const isStdio = type === 'stdio' && typeof obj.command === 'string'
          if (!isHttpLike && !isStdio) {
            logger.warn(
              {
                name: k,
                type,
                hasUrl: typeof obj.url === 'string',
                hasCommand: typeof obj.command === 'string',
              },
              '[claude] dropping in-process / unknown-shape mcpServers entry (SDK only accepts http/sse/stdio configs)',
            )
            continue
          }
          mcpServers[k] = v
        }
      }

      // ------------------------------------------------------------------
      // 4) Build the system-prompt preamble (first turn only)
      // ------------------------------------------------------------------
      let isResume = typeof opts.resume === 'string' && opts.resume !== ''
      let systemPreamble: string | undefined
      if (projectRepoDir !== undefined) {
        try {
          systemPreamble = await assembleSystemPrompt({ projectRepoDir, logger })
        } catch (err) {
          logger.warn(
            { err, projectRepoDir },
            '[claude] failed to assemble system prompt; proceeding with harness only',
          )
          try {
            systemPreamble = getHarness()
          } catch (harnessErr) {
            logger.error(
              { err: harnessErr },
              '[claude] failed to read platform harness; proceeding with no preamble',
            )
          }
        }
      } else {
        try {
          systemPreamble = getHarness()
        } catch (harnessErr) {
          logger.error(
            { err: harnessErr },
            '[claude] failed to read platform harness; proceeding with no preamble',
          )
        }
      }

      // ------------------------------------------------------------------
      // 5) Reasoning wiring (effort + adaptive thinking)
      // ------------------------------------------------------------------
      const effort = opts.reasoningEffort
      const supportsReasoning = modelSupportsReasoning(model)

      // ------------------------------------------------------------------
      // 6) Abort wiring — pass an AbortController to the SDK
      // ------------------------------------------------------------------
      const sdkAbort = new AbortController()
      const abortHandler = (): void => {
        sdkAbort.abort()
        if (activeQuery?.interrupt !== undefined) {
          activeQuery.interrupt().catch((err) => {
            logger.warn(
              { err },
              '[claude] query.interrupt() rejected — relying on abortController unwind',
            )
          })
        }
      }
      let activeQuery: ClaudeSdkQuery | undefined
      if (opts.abortSignal !== undefined) {
        if (opts.abortSignal.aborted) {
          restoreEnv()
          return
        }
        opts.abortSignal.addEventListener('abort', abortHandler, { once: true })
      }

      // ------------------------------------------------------------------
      // 7) Build options + run the query (with resume→fresh fallback)
      // ------------------------------------------------------------------
      const mapperState = makeClaudeMapperState()
      mapperState.lastModelId = model

      const buildOptions = (withPreamble: boolean, withResume: boolean): ClaudeQueryOptions => {
        const o: ClaudeQueryOptions = {
          model,
          cwd: opts.cwd,
          permissionMode: opts.yolo === true ? 'bypassPermissions' : 'acceptEdits',
          abortController: sdkAbort,
        }
        if (withResume && typeof opts.resume === 'string' && opts.resume !== '') {
          o.resume = opts.resume
        }
        if (Object.keys(mcpServers).length > 0) o.mcpServers = mcpServers
        if (effort !== undefined) o.effort = effort
        // adaptive thinking ONLY on reasoning-capable models; NEVER send
        // budget_tokens (it 400s on Opus 4.7/4.8 & Fable 5).
        if (supportsReasoning) o.thinking = { type: 'adaptive' }
        if (withPreamble && systemPreamble !== undefined && systemPreamble !== '') {
          o.systemPrompt = systemPreamble
        }
        return o
      }

      try {
        const sdk = await importSdk()

        // The prompt body is always the user's message — unlike Cursor (which
        // has no system-prompt knob and prepends the harness into the prompt),
        // the Claude SDK takes `systemPrompt` as a first-class option, so the
        // preamble rides there and the prompt stays clean.
        const runQuery = (withPreamble: boolean, withResume: boolean): ClaudeSdkQuery =>
          sdk.query({
            prompt: opts.prompt,
            options: buildOptions(withPreamble, withResume),
          })

        // On resume we omit the preamble (session carries context); on a fresh
        // run we include it.
        activeQuery = runQuery(/*withPreamble*/ !isResume, /*withResume*/ isResume)
        if (opts.abortSignal?.aborted === true) {
          sdkAbort.abort()
        }

        logger.info(
          { model, isResume, hasEffort: effort !== undefined, supportsReasoning },
          isResume ? 'claude.query.resume' : 'claude.query.create',
        )

        // Stream loop. We may need to restart the iterator once if a resume
        // hits "session not found".
        let attemptedFallback = false
        // Tracks whether a terminal Status (Finished / Error) reached the wire,
        // so the post-loop fallback only synthesises one when the stream closed
        // without it (parity with Cursor's synthesizeTerminalStatus path).
        let terminalEmitted = false

        // The SDK's `result` message is the authoritative terminal aggregate.
        // We intercept it BEFORE the mapper to derive the RunResultPayload +
        // capture usage, stage it, then let the mapper attach the run-result to
        // the Status frame it emits for the same `result` message.
        const streamOnce = async function* (
          q: ClaudeSdkQuery,
        ): AsyncGenerator<TurnEvent, void, void> {
          for await (const raw of q) {
            const msg = raw as ClaudeSdkMessage
            if ((msg as { type?: unknown }).type === 'result') {
              const runResultPayload = buildRunResultPayload({
                frame: msg as Parameters<typeof buildRunResultPayload>[0]['frame'],
                fallbackModelId: mapperState.lastModelId ?? model,
              })
              noteClaudeTerminalRunResult(mapperState, runResultPayload)
              // Capture usage for the dedicated cost channel (daemon-internal).
              const usage = extractUsage(msg as { usage?: unknown })
              if (usage !== undefined) noteClaudeUsage(mapperState, usage)
            }
            const wireEvents = mapClaudeMessage(msg, mapperState)
            for (const evt of wireEvents) {
              if (
                evt.kind === 'Status' &&
                (evt.runStatus === AgentEventRunStatus.Finished ||
                  evt.runStatus === AgentEventRunStatus.Error)
              ) {
                terminalEmitted = true
              }
              yield evt as TurnEvent
            }
          }
        }

        // Stream the query. On a resume that hits "session not found" we
        // restart ONCE with a fresh query (parity with Cursor's "agent not
        // found" → create fallback); the loop runs at most twice.
        for (;;) {
          try {
            yield* streamOnce(activeQuery)
            break
          } catch (streamErr) {
            const sdkMsg =
              streamErr instanceof Error ? streamErr.message : String(streamErr)
            const isNotFound =
              isResume &&
              !attemptedFallback &&
              /session[^]*not\s*found|no\s*such\s*session|resume[^]*not\s*found/i.test(
                sdkMsg,
              )
            if (!isNotFound) {
              throw streamErr
            }
            attemptedFallback = true
            isResume = false
            logger.warn(
              { err: streamErr, staleSessionId: opts.resume },
              '[claude] resume hit "session not found" — falling back to a fresh query()',
            )
            if (sdkAbort.signal.aborted) break
            // Fresh run: include the preamble, drop the resume id, then loop
            // back to stream the new query.
            activeQuery = runQuery(/*withPreamble*/ true, /*withResume*/ false)
          }
        }

        // If the stream produced no terminal Status (atypical — the SDK always
        // emits a `result`, but an abort can unwind before it), synthesise one
        // so the daemon's turn-completion bookkeeping always sees a terminal
        // frame. Mirrors Cursor's synthesizeTerminalStatus path. Treated as
        // Finished: the only non-abort way to land here is a clean stream
        // close, and the abort path is excluded by the guard.
        if (!terminalEmitted && opts.abortSignal?.aborted !== true) {
          yield synthesizeClaudeTerminalStatus(mapperState, {
            runStatus: AgentEventRunStatus.Finished,
          }) as TurnEvent
        }
      } catch (err) {
        logger.error(
          { err },
          'claude.factory.threw — emitting synthetic system error frame',
        )
        yield {
          kind: 'System',
          subtype: 'error',
          eventData: {
            error: err instanceof Error ? err.message : String(err),
          },
        } as TurnEvent
        throw err
      } finally {
        if (opts.abortSignal !== undefined) {
          opts.abortSignal.removeEventListener('abort', abortHandler)
        }
        restoreEnv()
      }
    },
  })
}

// ---------------------------------------------------------------------------
// RunResult / usage helpers
// ---------------------------------------------------------------------------

interface ClaudeResultFrameLike {
  type: 'result'
  duration_ms?: number
  modelUsage?: Record<string, { model?: string } & Record<string, unknown>>
  [k: string]: unknown
}

/**
 * Build the wire `RunResultPayload` from the SDK `result` message. The Claude
 * backend has no git/PR or artifact surface in Phase 1 (the daemon's own
 * GitModule handles commit/push), so `artifacts` is always empty and
 * gitBranch/gitPrUrl are omitted — the chat panel renders duration + model.
 */
function buildRunResultPayload(args: {
  frame: ClaudeResultFrameLike
  fallbackModelId: string
}): RunResultPayload {
  const { frame, fallbackModelId } = args
  let modelId = fallbackModelId
  // modelUsage keys are the model ids actually used; take the first.
  const mu = frame.modelUsage
  if (mu !== undefined && mu !== null && typeof mu === 'object') {
    const firstKey = Object.keys(mu)[0]
    if (typeof firstKey === 'string' && firstKey !== '') {
      modelId = firstKey
    }
  }
  return {
    durationMs: typeof frame.duration_ms === 'number' ? frame.duration_ms : 0,
    model: modelId,
    artifacts: [],
  }
}

/** Extract normalized token usage from the SDK `result` message's `usage`. */
function extractUsage(frame: { usage?: unknown }):
  | {
      inputTokens: number
      outputTokens: number
      cacheReadTokens: number
      cacheWriteTokens: number
    }
  | undefined {
  const u = frame.usage
  if (u === null || typeof u !== 'object') return undefined
  const usage = u as Record<string, unknown>
  const num = (v: unknown): number => (typeof v === 'number' ? v : 0)
  return {
    inputTokens: num(usage['input_tokens']),
    outputTokens: num(usage['output_tokens']),
    cacheReadTokens: num(usage['cache_read_input_tokens']),
    cacheWriteTokens: num(usage['cache_creation_input_tokens']),
  }
}

// ---------------------------------------------------------------------------
// System-prompt assembly (parity with CursorFactory)
// ---------------------------------------------------------------------------

/**
 * Assemble the system-prompt preamble: platform harness + project rules
 * (`.claude/rules/*.md` and `.cursor/rules/*.md`). Read errors on individual
 * rule files are logged + swallowed.
 */
async function assembleSystemPrompt(args: {
  projectRepoDir: string
  logger: Logger
}): Promise<string | undefined> {
  const { projectRepoDir, logger } = args
  const parts: string[] = []
  const harness = getHarness()
  if (harness !== '') parts.push(harness)

  const claudeRules = await readRulesDir(
    path.join(projectRepoDir, '.claude', 'rules'),
    logger,
  )
  if (claudeRules.length > 0) {
    parts.push('# Project rules (.claude/rules)', ...claudeRules)
  }
  const cursorRules = await readRulesDir(
    path.join(projectRepoDir, '.cursor', 'rules'),
    logger,
  )
  if (cursorRules.length > 0) {
    parts.push('# Project rules (.cursor/rules)', ...cursorRules)
  }

  if (parts.length === 0) return undefined
  return parts.join('\n\n')
}

async function readRulesDir(dir: string, logger: Logger): Promise<string[]> {
  let entries: string[]
  try {
    entries = await readdir(dir)
  } catch (err) {
    const code = (err as NodeJS.ErrnoException).code
    if (code === 'ENOENT' || code === 'ENOTDIR') return []
    logger.warn({ err, dir }, '[claude] failed to list rules dir')
    return []
  }
  const mdFiles = entries.filter((n) => n.endsWith('.md')).sort()
  const contents: string[] = []
  for (const name of mdFiles) {
    const file = path.join(dir, name)
    try {
      contents.push(await readFile(file, 'utf8'))
    } catch (err) {
      logger.warn({ err, file }, '[claude] failed to read rule file')
    }
  }
  return contents
}

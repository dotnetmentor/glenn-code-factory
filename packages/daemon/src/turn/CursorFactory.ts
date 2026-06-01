// CursorFactory — the thin seam between TurnRunner and `@cursor/sdk`.
//
// Build an `AgentFactory` closure bound to per-daemon dependencies (MCP
// registry, runtime token getter, project repo dir, git branch resolver);
// the closure is invoked per turn by `TurnRunner` and yields a stream of
// wire `TurnEvent` frames that `TurnRunner`'s inline projector forwards onto
// the hub.
//
// === Turn lifecycle ===
//
// The Cursor SDK exposes an `Agent.create()` / `Agent.resume(agentId)`
// pattern. The agent is a persistent SDK-side object: every `send()`
// produces a `Run`, runs persist across calls and can be resumed. We
// translate this into the daemon's "one stream per turn" model:
//
//   1. Pick `Agent.create()` (fresh conversation) or `Agent.resume(opts.resume)`
//      (continuing a previous Cursor agent — `opts.resume` is the SDK's
//      `agentId`, captured from a previous turn's `system: init` frame).
//   2. Call `agent.send(prompt)` → returns a `Run`.
//   3. Iterate `run.stream()` and translate each Cursor `SDKMessage` to the
//      daemon's intermediate `TurnEvent` shape via `mapCursorMessage`.
//   4. On abort: call `run.cancel()` and let the stream unwind.
//   5. On exit: call `agent.close()` so the SDK releases the local-runtime
//      process handle.
//
// === System prompt handling ===
//
// `@cursor/sdk`'s `AgentOptions` has no system-prompt knob, so we prepend
// the platform harness + project rules to the user's prompt on the FIRST
// turn (no resume hint) and let the agent's persistent context carry it
// across resumed turns.
//
// === BYOK handoff ===
//
// Cursor reads `process.env.CURSOR_API_KEY` when `apiKey` is omitted on
// `AgentOptions`. We do both: set `apiKey` on the options AND scope the
// env var around the SDK call. The duplication is intentional — `apiKey`
// wins per the SDK's own resolution, but if a future SDK version drops
// `apiKey` in favor of env-only auth, the env handoff catches that.
//
// === MCP servers ===
//
// `AgentOptions.mcpServers` is a `Record<string, McpServerConfig>` where
// each entry can be `{ type: 'http', url, headers }`. We project the
// daemon's MCP registry entries (project-scoped kanban / planning / etc.)
// into that shape, attaching the runtime token as a Bearer header and the
// git branch as `X-Daemon-Git-Branch` for kanban-card-provenance.
//
// === Lazy SDK import ===
//
// The SDK pulls in a non-trivial dependency graph; we want quiet-mode to
// be able to drop the module reference and let GC reclaim the memory.
// Per-call dynamic import.

import type { Logger } from 'pino'
import { readdir, readFile } from 'node:fs/promises'
import path from 'node:path'

import type { TurnEvent } from './TurnEvent.js'
import type { AgentFactory } from './AgentFactory.js'
import type { TurnOptions } from './TurnOptions.js'
import type { McpRegistry } from '../mcp/McpRegistry.js'
import { getHarness } from '../harness/index.js'
import {
  makeCursorMapperState,
  mapCursorMessage,
  noteTerminalRunResult,
  noteTurnEndedUsage,
  synthesizeTerminalStatus,
  type CursorSdkMessage,
} from './CursorEventMapper.js'
import { AgentEventRunStatus } from '../signalr/types.js'
import type {
  RunArtifactPayload,
  RunResultPayload,
} from '../generated/signalr/Source.Features.SignalR.Contracts.js'

// We avoid `import type * as CursorSdk from '@cursor/sdk'` here. The mapper
// module already declares its own structural types so this file can stay
// version-tolerant; the `importSdk` test seam injects a stub module shape
// rather than relying on the real @cursor/sdk export surface.

/**
 * Minimal module shape we need from `@cursor/sdk`. Kept narrow so a future
 * SDK version that adds new exports doesn't break us, and so the test seam
 * `importSdk` can return a hand-rolled stub.
 */
export interface CursorSdkModule {
  Agent: {
    create(options: CursorAgentOptions): Promise<CursorSdkAgent>
    resume(
      agentId: string,
      options?: Partial<CursorAgentOptions>,
    ): Promise<CursorSdkAgent>
  }
}

/**
 * The subset of `@cursor/sdk`'s `SDKAgent` we use. Same justification as
 * `CursorSdkModule` — tolerant typing keeps the factory compileable across
 * SDK minor versions.
 */
export interface CursorSdkAgent {
  readonly agentId: string
  send(message: string, options?: CursorSendOptions): Promise<CursorSdkRun>
  close(): void
  /**
   * List the run's artifact files surfaced by the local-runtime side. Used
   * post-stream to stamp the terminal Status frame's `runResult.artifacts`
   * field. Older SDK versions / cloud agents may not implement this — the
   * factory tolerates that with a try/catch.
   */
  listArtifacts?(): Promise<CursorSdkArtifact[]>
}

export interface CursorSdkArtifact {
  path: string
  sizeBytes: number
  /** ISO 8601 string per SDK shape. */
  updatedAt: string
}

export interface CursorRunGitBranchInfo {
  repoUrl?: string
  branch?: string
  prUrl?: string
}

export interface CursorRunGitInfo {
  branches?: CursorRunGitBranchInfo[]
}

export interface CursorRunResult {
  id: string
  status: 'finished' | 'error' | 'cancelled'
  result?: string
  /** ModelSelection-ish — the SDK uses `{ id, params? }`. */
  model?: { id?: string } | string
  durationMs?: number
  git?: CursorRunGitInfo
}

export interface CursorSdkRun {
  readonly id: string
  readonly agentId: string
  stream(): AsyncGenerator<CursorSdkMessage, void>
  cancel(): Promise<void>
  wait?(): Promise<CursorRunResult>
  supports?(operation: 'stream' | 'wait' | 'cancel' | 'conversation'): boolean
}

/**
 * The subset of `@cursor/sdk`'s `AgentOptions` we set. The full SDK shape
 * has more fields (sandboxOptions, idempotencyKey, platform overrides, …)
 * which we leave at SDK defaults.
 */
export interface CursorAgentOptions {
  /**
   * `{ id, params? }` — required for local agents. We carry the model string
   * straight from `opts.model`; the params field is optional and we leave it
   * out (the SDK falls back to the model's defaults).
   */
  model?: { id: string; params?: Array<{ id: string; value: string }> }
  /**
   * BYOK. When set, the SDK uses this directly instead of consulting
   * `process.env.CURSOR_API_KEY`. We set BOTH (see env handoff comment in
   * the closure below).
   */
  apiKey?: string
  /**
   * Local-agent options. `cwd` pins the agent's working directory to the
   * repo mount point; `settingSources` enables `.cursor/rules` loading from
   * the project.
   */
  local?: {
    cwd?: string | string[]
    settingSources?: Array<
      'project' | 'user' | 'team' | 'mdm' | 'plugins' | 'all'
    >
  }
  /**
   * Per-agent MCP server registrations. `Record<name, McpServerConfig>`; the
   * SDK passes each to its own MCP client wiring.
   */
  mcpServers?: Record<
    string,
    | { type?: 'http' | 'sse'; url: string; headers?: Record<string, string> }
    | { type?: 'stdio'; command: string; args?: string[]; env?: Record<string, string>; cwd?: string }
  >
  /**
   * Subagent definitions. Cursor's subagent shape includes a `prompt` (the
   * subagent's system prompt) and an `mcpServers` allowlist. We don't wire
   * platform subagents for Cursor today — the user's existing `task` tool
   * delegations from prior backends carry over via the harness preamble.
   */
  agents?: Record<
    string,
    {
      description: string
      prompt: string
      model?: { id: string; params?: Array<{ id: string; value: string }> } | 'inherit'
      mcpServers?: Array<string | Record<string, unknown>>
    }
  >
}

export interface CursorSendOptions {
  model?: { id: string; params?: Array<{ id: string; value: string }> }
  mcpServers?: CursorAgentOptions['mcpServers']
  idempotencyKey?: string
  local?: {
    /**
     * Force-recover from a wedged previous run. We do NOT use this — the
     * daemon's per-turn agent lifecycle is short-lived and we always close
     * the agent in our `finally` block.
     */
    force?: boolean
  }
  /**
   * Per-update callback for the SDK's `InteractionUpdate` stream. The SDK
   * emits one update per protobuf `AgentClientMessage.message`; the only
   * variant we care about is `turn-ended` (carries per-turn token usage
   * that `run.stream()` does NOT surface — see the factory's onDelta
   * wiring + CursorEventMapper.noteTurnEndedUsage for why).
   */
  onDelta?: (args: { update: { type: string; [k: string]: unknown } }) => void | Promise<void>
}

export interface BuildCursorFactoryDeps {
  /** Logger threaded through to the factory itself. */
  logger: Logger
  /**
   * Project-scoped MCP snapshot from `BootstrapMcpStage`. Read on every
   * turn so future live-update paths land automatically.
   *
   * CAPABILITY: supportsMcpHttp
   */
  mcpRegistry?: Pick<McpRegistry, 'entries'>
  /**
   * Runtime token resolver — the daemon's `RuntimeToken` getter. Attached
   * as the `Authorization: Bearer ...` header on every MCP HTTP server we
   * project from the registry.
   *
   * CAPABILITY: supportsMcpHttp
   */
  getRuntimeToken?: () => string
  /**
   * Git branch resolver for the `X-Daemon-Git-Branch` MCP header. Resolved
   * once per turn. `null` / empty string means "no branch" and the header
   * is omitted.
   *
   * CAPABILITY: supportsMcpHttp
   */
  getGitBranch?: () => Promise<string | null>
  /**
   * Absolute path to the project repo mount point. Used to read project
   * rule files (`.cursor/rules/*.md`) for the system-prompt prepend.
   *
   * CAPABILITY: supportsSystemPrompt
   */
  projectRepoDir?: string
  /**
   * Default model selection when `opts.model` is undefined. Cursor REQUIRES
   * a model for local agents — there's no server-side default to fall back
   * on (unlike cloud). Production wiring passes `{ id: 'auto' }` or whatever
   * the project envelope resolved. Test seams can omit this and supply a
   * model on every call via `opts.model`.
   */
  defaultModel?: { id: string; params?: Array<{ id: string; value: string }> }
  /**
   * Test seam — replace the SDK import with a stub. Production omits this
   * and the factory does `() => import('@cursor/sdk')`.
   */
  importSdk?: () => Promise<CursorSdkModule>
}

/**
 * Build the Cursor `AgentFactory`. Production wiring (`main.ts`) hands this
 * directly to `TurnRunner` — Cursor is the only supported runtime, so there
 * is no agent-kind branching above this seam.
 */
export function buildCursorFactory(deps: BuildCursorFactoryDeps): AgentFactory {
  const logger = deps.logger.child({ module: 'cursor-factory' })
  const projectRepoDir = deps.projectRepoDir
  const defaultModel = deps.defaultModel
  // Production: dynamic import of @cursor/sdk. The cast through `unknown` is
  // because we use the narrow structural `CursorSdkModule` rather than the
  // full SDK export surface — this lets us swap test stubs in without
  // worrying about the closed shape of `typeof import('@cursor/sdk')`.
  const importSdk: () => Promise<CursorSdkModule> =
    deps.importSdk ??
    (async () => (await import('@cursor/sdk')) as unknown as CursorSdkModule)

  return (opts: TurnOptions) => ({
    [Symbol.asyncIterator]: async function* (): AsyncGenerator<TurnEvent, void, void> {
      // ------------------------------------------------------------------
      // 1) BYOK env handoff
      // ------------------------------------------------------------------
      // Cursor reads `process.env.CURSOR_API_KEY` when `apiKey` is omitted.
      // We snapshot the daemon's existing env, scope an override around the
      // SDK call, and restore on exit. We also set `apiKey` on
      // AgentOptions — the SDK prefers the explicit field, but if a future
      // version drops `apiKey` in favor of env-only auth, the scoped env
      // override catches that.
      const cursorApiKey = opts.secrets?.cursorApiKey ?? null
      const priorEnvValue = process.env['CURSOR_API_KEY']
      const restoreEnv = (): void => {
        if (priorEnvValue === undefined) {
          delete process.env['CURSOR_API_KEY']
        } else {
          process.env['CURSOR_API_KEY'] = priorEnvValue
        }
      }
      if (cursorApiKey !== null && cursorApiKey !== '') {
        process.env['CURSOR_API_KEY'] = cursorApiKey
      } else {
        // Explicitly absent — the SDK's auth check should surface a clean
        // "missing API key" error rather than picking up a stale daemon-env
        // value the user didn't intend.
        delete process.env['CURSOR_API_KEY']
      }

      // ------------------------------------------------------------------
      // 2) Resolve model
      // ------------------------------------------------------------------
      // Cursor local agents REQUIRE a model selection — there's no server-
      // side default. We prefer `opts.model` (from StartTurnPayload),
      // falling back to the factory-level default. If neither is set we
      // throw early with a clear message — the alternative (letting the SDK
      // throw deep inside its own initializer) is worse for users.
      const modelOpt = opts.model
      let model: CursorAgentOptions['model']
      if (typeof modelOpt === 'string' && modelOpt !== '') {
        model = { id: modelOpt }
      } else if (defaultModel !== undefined) {
        model = defaultModel
      } else {
        // No model — fail fast. The downstream catch path in TurnRunner
        // stamps `sdk_error` and the user sees a real error rather than a
        // silent hang.
        restoreEnv()
        throw new Error(
          'CursorFactory: no model resolved — neither opts.model nor defaultModel was set',
        )
      }

      // ------------------------------------------------------------------
      // 3) Build MCP servers map
      // ------------------------------------------------------------------
      // CAPABILITY: supportsMcpHttp
      // Project the daemon's MCP registry entries into Cursor's
      // `Record<string, McpServerConfig>` shape. The branch + token are
      // resolved ONCE per turn.
      const mcpEntries = deps.mcpRegistry?.entries() ?? []
      const mcpServers: CursorAgentOptions['mcpServers'] = {}
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
      // Overlay any per-turn MCP servers TurnRunner passed in — BUT only the
      // declarative transport shapes Cursor knows how to wire over its RPC
      // bridge: { type: 'http'|'sse', url, headers } or
      // { type: 'stdio', command, args, env, cwd }.
      //
      // We must skip in-process server INSTANCES (e.g. the daemon-tools MCP
      // server built by `buildDaemonToolsServer` and attached by TurnRunner
      // under the DAEMON_TOOLS_SERVER_NAME key). Cursor's SDK doesn't accept
      // in-process Server objects — and the daemon-tools server has circular
      // references back into its own tool-registry / transport plumbing,
      // which makes Cursor's MCP wiring (which deep-clones options for the
      // local-runtime spawn) recurse infinitely and crash the turn with
      // "Maximum call stack size exceeded".
      //
      // The filter is structural: any entry that is NOT a plain config object
      // (no `url` for http/sse, no `command` for stdio) is dropped with a warn.
      if (opts.mcpServers !== undefined) {
        for (const [k, v] of Object.entries(opts.mcpServers)) {
          if (v === null || typeof v !== 'object') {
            logger.warn(
              { name: k, valueType: typeof v },
              '[cursor] dropping non-object mcpServers entry',
            )
            continue
          }
          const obj = v as Record<string, unknown>
          const type = typeof obj.type === 'string' ? (obj.type as string) : undefined
          const isHttpLike =
            (type === 'http' || type === 'sse' || type === undefined) &&
            typeof obj.url === 'string'
          const isStdio =
            type === 'stdio' && typeof obj.command === 'string'
          if (!isHttpLike && !isStdio) {
            // Most likely an in-process Server INSTANCE. Cursor can't
            // transport that — skip with a warn so we don't blow up.
            logger.warn(
              { name: k, type, hasUrl: typeof obj.url === 'string', hasCommand: typeof obj.command === 'string' },
              '[cursor] dropping in-process / unknown-shape mcpServers entry (Cursor only accepts http/sse/stdio configs)',
            )
            continue
          }
          mcpServers[k] =
            v as NonNullable<CursorAgentOptions['mcpServers']>[string]
        }
      }

      // ------------------------------------------------------------------
      // 4) Build the prompt body
      // ------------------------------------------------------------------
      // CAPABILITY: supportsSystemPrompt
      // Cursor has no system-prompt option, so we prepend the platform
      // harness + project rules to the user's first turn. On resume turns
      // (when `opts.resume` is non-empty) the agent already has its
      // conversation context — we send only the user's prompt to avoid
      // re-injecting the harness on every turn (token waste + risks model
      // dropping focus on the user's current message).
      let isResume =
        typeof opts.resume === 'string' && opts.resume !== ''

      // Build BOTH prompt-body variants up-front so the resume→create
      // fallback path (below, on "Agent X not found") can swap to the
      // fresh-agent body without re-doing the async preamble work inside the
      // catch. The bare-prompt variant goes out on resume; the
      // preamble-bearing variant goes out on create.
      let systemPreamble: string | undefined
      if (projectRepoDir !== undefined) {
        try {
          systemPreamble = await assembleSystemPrompt({
            projectRepoDir,
            logger,
          })
        } catch (err) {
          // Don't fail the turn on a rules-read error — log and proceed
          // with the harness only (or no preamble if even that fails).
          logger.warn(
            { err, projectRepoDir },
            '[cursor] failed to assemble system prompt; proceeding without project rules',
          )
          try {
            systemPreamble = getHarness()
          } catch (harnessErr) {
            logger.error(
              { err: harnessErr },
              '[cursor] failed to read platform harness; proceeding with bare prompt',
            )
          }
        }
      } else {
        // No project dir — at least include the harness so platform
        // invariants land in the model.
        try {
          systemPreamble = getHarness()
        } catch (harnessErr) {
          logger.error(
            { err: harnessErr },
            '[cursor] failed to read platform harness; proceeding with bare prompt',
          )
        }
      }
      const freshPromptBody =
        systemPreamble !== undefined && systemPreamble !== ''
          ? `${systemPreamble}\n\n---\n\n${opts.prompt}`
          : opts.prompt
      let promptBody: string = isResume ? opts.prompt : freshPromptBody

      // ------------------------------------------------------------------
      // 5) Build AgentOptions
      // ------------------------------------------------------------------
      const agentOptions: CursorAgentOptions = {
        model,
        local: {
          cwd: opts.cwd,
          // settingSources: ['project'] enables Cursor's native
          // .cursor/rules loading. We always include it on local agents so
          // a project that wrote rules for Cursor gets them automatically.
          settingSources: ['project'],
        },
      }
      if (cursorApiKey !== null && cursorApiKey !== '') {
        agentOptions.apiKey = cursorApiKey
      }
      if (Object.keys(mcpServers).length > 0) {
        agentOptions.mcpServers = mcpServers
      }

      // ------------------------------------------------------------------
      // 6) Wire abort → run.cancel()
      // ------------------------------------------------------------------
      // The Cursor SDK exposes `run.cancel()` but takes no AbortSignal
      // directly. We bridge: if the daemon's abort signal fires, we call
      // `run.cancel()` and let the stream unwind. The `run` reference is
      // captured below as soon as `agent.send()` resolves.
      let activeRun: CursorSdkRun | undefined
      const abortHandler = (): void => {
        if (activeRun !== undefined) {
          activeRun.cancel().catch((err) => {
            logger.warn(
              { err, runId: activeRun?.id, agentId: activeRun?.agentId },
              '[cursor] run.cancel() rejected — propagating via stream unwind',
            )
          })
        }
      }
      if (opts.abortSignal !== undefined) {
        if (opts.abortSignal.aborted) {
          // Already aborted before we even started — don't bother creating
          // the agent.
          restoreEnv()
          return
        }
        opts.abortSignal.addEventListener('abort', abortHandler, { once: true })
      }

      // ------------------------------------------------------------------
      // 7) Create/Resume agent, send prompt, stream
      // ------------------------------------------------------------------
      let agent: CursorSdkAgent | undefined
      const mapperState = makeCursorMapperState()
      try {
        const sdk = await importSdk()
        // CAPABILITY: supportsSessionResume
        // `Agent.resume(agentId)` re-attaches to a previously-created agent
        // by its SDK-assigned id. The agent's history is preserved on the
        // local-runtime side so the next `send()` continues the
        // conversation. When `isResume` is false we create a fresh one.
        if (isResume) {
          // resume() accepts a Partial<AgentOptions> — we forward the
          // per-turn overrides (model, mcpServers, apiKey) so a turn that
          // changes model takes effect on resume. The local.cwd CANNOT
          // change on resume (the agent's working dir is pinned at create
          // time); we still pass it to be tolerant — the SDK ignores it.
          try {
            agent = await sdk.Agent.resume(opts.resume as string, agentOptions)
            logger.info(
              { agentId: agent.agentId, resumed: true },
              'cursor.agent.resume',
            )
          } catch (resumeErr) {
            // SDK throws `Agent agent-<uuid> not found` when the local agent
            // store has been wiped (Fly machine respawn without the volume
            // mount preserving /data, manual `cursorsandbox` reset, etc).
            // The saved agentId is now a dangling reference. Falling back
            // to a fresh agent loses the local conversation history but
            // unblocks the turn — without this, every follow-up message
            // post-respawn would hard-fail with `sdk_error`. The platform's
            // own conversation history is preserved in AgentSessions /
            // AgentEvents, so the operator-visible record is intact;
            // only the SDK's own per-agent context is lost.
            const msg = resumeErr instanceof Error ? resumeErr.message : String(resumeErr)
            const isNotFound = /agent\s+[^\s]+\s+not\s+found/i.test(msg)
            if (!isNotFound) {
              throw resumeErr
            }
            logger.warn(
              { err: resumeErr, staleAgentId: opts.resume },
              '[cursor] resume hit "agent not found" — falling back to create()',
            )
            // Swap to the fresh-agent prompt body (includes the harness +
            // project rules preamble) and create a new agent.
            promptBody = freshPromptBody
            isResume = false
            agent = await sdk.Agent.create(agentOptions)
            logger.info(
              { agentId: agent.agentId, resumed: false, recoveredFromMissingAgent: true },
              'cursor.agent.create (fallback)',
            )
          }
        } else {
          agent = await sdk.Agent.create(agentOptions)
          logger.info(
            { agentId: agent.agentId, resumed: false },
            'cursor.agent.create',
          )
        }

        // Subscribe to `InteractionUpdate`s so we can capture the
        // `turn-ended` token usage frame. `run.stream()` silently drops
        // turn-ended (the SDK's interactionUpdateToSdkMessage returns
        // undefined for it), so this is the ONLY surface that gives us
        // per-turn token counts. See CursorEventMapper.noteTurnEndedUsage
        // for the wire-shape translation.
        const run = await agent.send(promptBody, {
          onDelta: ({ update }) => {
            if (
              update !== null &&
              typeof update === 'object' &&
              (update as { type?: unknown }).type === 'turn-ended'
            ) {
              const usage = (update as { usage?: unknown }).usage
              if (
                usage !== null &&
                typeof usage === 'object' &&
                typeof (usage as { inputTokens?: unknown }).inputTokens === 'number' &&
                typeof (usage as { outputTokens?: unknown }).outputTokens === 'number' &&
                typeof (usage as { cacheReadTokens?: unknown }).cacheReadTokens === 'number' &&
                typeof (usage as { cacheWriteTokens?: unknown }).cacheWriteTokens === 'number'
              ) {
                noteTurnEndedUsage(mapperState, usage as {
                  inputTokens: number
                  outputTokens: number
                  cacheReadTokens: number
                  cacheWriteTokens: number
                })
                logger.debug(
                  {
                    inputTokens: (usage as { inputTokens: number }).inputTokens,
                    outputTokens: (usage as { outputTokens: number }).outputTokens,
                    cacheReadTokens: (usage as { cacheReadTokens: number }).cacheReadTokens,
                    cacheWriteTokens: (usage as { cacheWriteTokens: number }).cacheWriteTokens,
                  },
                  'cursor.turn_ended.usage_captured',
                )
              }
            }
          },
        })
        activeRun = run
        logger.debug(
          { agentId: agent.agentId, runId: run.id },
          'cursor.run.started',
        )

        // CAPABILITY: supportsAbort
        // If the signal fired between send() resolving and the loop
        // starting, propagate now — don't enter the stream loop just to
        // hit an early abort.
        if (opts.abortSignal?.aborted === true) {
          await run.cancel().catch(() => {})
        }

        // Iterate the run stream. Each SDKMessage goes through the mapper
        // which yields zero or more cursor-native wire events. The mapper
        // handles the tool_call lifecycle dedupe and shape translation.
        //
        // We BUFFER any terminal Status frame we see in-stream so we can
        // attach the wait()-derived `runResult` aggregate (durationMs, model,
        // gitBranch, gitPrUrl, artifacts) BEFORE the terminal status lands on
        // the wire. The mapper's `mapStatus()` drains `pendingRunResult` from
        // the state at emit time — so the contract is:
        //
        //   1. Stream → maybe emits terminal Status (we hold it back).
        //   2. Run wait() + listArtifacts() → build RunResultPayload.
        //   3. Stage via noteTerminalRunResult() → mapper will attach on next
        //      terminal Status emission OR synthesizeTerminalStatus() pulls
        //      it directly when the stream never produced one.
        //   4. Emit synthesized terminal Status carrying the staged result.
        //
        // The buffering means a SDK that emits an in-stream terminal status
        // and a stream that never does both yield exactly one terminal event
        // on the wire, with runResult always attached.
        let bufferedTerminalStatus:
          | {
              runStatus: AgentEventRunStatus
              statusMessage?: string
              agentId?: string
            }
          | undefined
        for await (const cursorMsg of run.stream()) {
          // Defensive: the SDK's static SDKMessage union is wider than the
          // mapper's tolerant CursorSdkMessage view; the cast is safe in
          // practice because mapCursorMessage narrows on `frame.type` at
          // runtime.
          const wireEvents = mapCursorMessage(
            cursorMsg as unknown as CursorSdkMessage,
            mapperState,
          )
          for (const evt of wireEvents) {
            // Buffer terminal Status frames so we can stamp the runResult
            // aggregate after wait() resolves. Non-terminal Status frames
            // (CREATING/RUNNING) flow through verbatim — they're the
            // progress pills the chat panel renders.
            if (
              evt.kind === 'Status' &&
              isTerminalRunStatus(evt.runStatus)
            ) {
              const buf: {
                runStatus: AgentEventRunStatus
                statusMessage?: string
                agentId?: string
              } = { runStatus: evt.runStatus }
              if (evt.statusMessage !== undefined) {
                buf.statusMessage = evt.statusMessage
              }
              if (evt.agentId !== undefined) {
                buf.agentId = evt.agentId
              }
              bufferedTerminalStatus = buf
              continue
            }
            yield evt as TurnEvent
          }
        }

        // Official SDK pattern: after the stream drains, `run.wait()` is the
        // authoritative terminal status + duration source. The stream may
        // close without a terminal `status` frame; wait() also catches cases
        // where assistant content streamed but the run ultimately failed.
        if (opts.abortSignal?.aborted !== true) {
          const supportsWait =
            typeof run.supports === 'function' ? run.supports('wait') : true
          const waitFn = run.wait
          if (supportsWait && typeof waitFn === 'function') {
            try {
              const runResult = await waitFn.call(run)
              // Build the RunResultPayload from the wait result + artifacts.
              const runResultPayload = await buildRunResultPayload({
                runResult,
                agent,
                fallbackModelId: typeof model.id === 'string' ? model.id : '',
                logger,
              })
              noteTerminalRunResult(mapperState, runResultPayload)

              // Determine the terminal RunStatus: prefer the in-stream
              // status (more accurate — Cursor emits CANCELLED/EXPIRED
              // distinctly), fall back to wait()'s status field which is
              // only finished/error/cancelled.
              const terminalRunStatus =
                bufferedTerminalStatus?.runStatus ??
                mapWaitStatus(runResult.status)
              const terminalArgs: {
                runStatus: AgentEventRunStatus
                agentId?: string
                statusMessage?: string
              } = { runStatus: terminalRunStatus }
              const terminalAgentId =
                bufferedTerminalStatus?.agentId ?? agent.agentId
              if (terminalAgentId !== undefined) {
                terminalArgs.agentId = terminalAgentId
              }
              if (bufferedTerminalStatus?.statusMessage !== undefined) {
                terminalArgs.statusMessage = bufferedTerminalStatus.statusMessage
              }
              const terminalEvent = synthesizeTerminalStatus(
                mapperState,
                terminalArgs,
              )
              yield terminalEvent as TurnEvent
              logger.debug(
                {
                  runId: run.id,
                  status: runResult.status,
                  durationMs: runResult.durationMs,
                  hadBufferedTerminal: bufferedTerminalStatus !== undefined,
                  artifactCount: runResultPayload.artifacts.length,
                },
                'cursor.run.wait_completed',
              )
            } catch (waitErr) {
              logger.warn(
                { err: waitErr, runId: run.id, agentId: agent.agentId },
                '[cursor] run.wait() failed',
              )
              // If the stream gave us a terminal status, flush it without a
              // runResult — better partial than missing.
              if (bufferedTerminalStatus !== undefined) {
                const flushArgs: {
                  runStatus: AgentEventRunStatus
                  agentId?: string
                  statusMessage?: string
                } = { runStatus: bufferedTerminalStatus.runStatus }
                if (bufferedTerminalStatus.agentId !== undefined) {
                  flushArgs.agentId = bufferedTerminalStatus.agentId
                }
                if (bufferedTerminalStatus.statusMessage !== undefined) {
                  flushArgs.statusMessage = bufferedTerminalStatus.statusMessage
                }
                yield synthesizeTerminalStatus(mapperState, flushArgs) as TurnEvent
              } else {
                yield {
                  kind: 'System',
                  subtype: 'error',
                  eventData: {
                    error:
                      waitErr instanceof Error ? waitErr.message : String(waitErr),
                  },
                  ...(agent.agentId !== undefined ? { agentId: agent.agentId } : {}),
                } as TurnEvent
                throw waitErr
              }
            }
          } else if (bufferedTerminalStatus !== undefined) {
            // No wait() support but we have an in-stream terminal status —
            // flush it with no runResult aggregate.
            const flushArgs: {
              runStatus: AgentEventRunStatus
              agentId?: string
              statusMessage?: string
            } = { runStatus: bufferedTerminalStatus.runStatus }
            if (bufferedTerminalStatus.agentId !== undefined) {
              flushArgs.agentId = bufferedTerminalStatus.agentId
            }
            if (bufferedTerminalStatus.statusMessage !== undefined) {
              flushArgs.statusMessage = bufferedTerminalStatus.statusMessage
            }
            yield synthesizeTerminalStatus(mapperState, flushArgs) as TurnEvent
          }
        }
      } catch (err) {
        // Surface as a System carrier so the audit row records it; TurnRunner's
        // catch path also stamps the overall `reason='sdk_error'`. We rethrow
        // so the catch path runs.
        logger.error(
          { err, agentId: agent?.agentId },
          'cursor.factory.threw — emitting synthetic system error frame',
        )
        yield {
          kind: 'System',
          subtype: 'error',
          eventData: {
            error: err instanceof Error ? err.message : String(err),
          },
          ...(agent?.agentId !== undefined ? { agentId: agent.agentId } : {}),
        } as TurnEvent
        throw err
      } finally {
        if (opts.abortSignal !== undefined) {
          opts.abortSignal.removeEventListener('abort', abortHandler)
        }
        // Close the agent so the SDK releases its local-runtime process
        // handle. Best-effort: throwing close() shouldn't mask a real
        // turn-level error.
        if (agent !== undefined) {
          try {
            agent.close()
          } catch (closeErr) {
            logger.debug(
              { err: closeErr, agentId: agent.agentId },
              'cursor.agent.close threw — ignoring',
            )
          }
        }
        restoreEnv()
      }
    },
  })
}

/**
 * Assemble the system-prompt preamble for a Cursor turn.
 *
 * Cursor's `settingSources: ['project']` natively loads `.cursor/rules/*.md`
 * from the project dir into the agent's context, but it has no notion of
 * our platform harness. So we hand-assemble:
 *
 *   1. Platform harness (`getHarness()`) — environment, tools, workflow.
 *   2. `.cursor/rules/*.md` — Cursor-specific project rules.
 *
 * The composed string is prepended to the user's first turn prompt
 * (see closure body). Read errors on individual rule files are logged
 * and swallowed — a missing rules file should never fail the turn.
 *
 * Returns the composed string, or `undefined` if literally nothing was
 * assembled (impossible today because the harness is always non-empty,
 * but the signature stays honest for future cases where harness is empty
 * in tests).
 *
 * CAPABILITY: supportsSystemPrompt
 */
async function assembleSystemPrompt(args: {
  projectRepoDir: string
  logger: Logger
}): Promise<string | undefined> {
  const { projectRepoDir, logger } = args

  const parts: string[] = []
  const harness = getHarness()
  if (harness !== '') {
    parts.push(harness)
  }

  const cursorRules = await readRulesDir(path.join(projectRepoDir, '.cursor', 'rules'), logger)
  if (cursorRules.length > 0) {
    parts.push('# Project rules (.cursor/rules)', ...cursorRules)
  }

  if (parts.length === 0) return undefined
  return parts.join('\n\n')
}

/**
 * Read every `*.md` file in `dir`, alphabetic order. Missing directory →
 * empty array (the common case for projects that don't ship rules).
 * Other errors are logged at warn level and produce empty array — we never
 * throw from here.
 */
async function readRulesDir(dir: string, logger: Logger): Promise<string[]> {
  let entries: string[]
  try {
    entries = await readdir(dir)
  } catch (err) {
    const code = (err as NodeJS.ErrnoException).code
    if (code === 'ENOENT' || code === 'ENOTDIR') return []
    logger.warn({ err, dir }, '[cursor] failed to list rules dir')
    return []
  }

  const mdFiles = entries.filter((n) => n.endsWith('.md')).sort()
  const contents: string[] = []
  for (const name of mdFiles) {
    const file = path.join(dir, name)
    try {
      const text = await readFile(file, 'utf8')
      contents.push(text)
    } catch (err) {
      logger.warn({ err, file }, '[cursor] failed to read rule file')
    }
  }
  return contents
}

// ---------------------------------------------------------------------------
// Terminal status / RunResult helpers
// ---------------------------------------------------------------------------

/** True when the RunStatus is one the daemon treats as terminal. */
function isTerminalRunStatus(s: AgentEventRunStatus): boolean {
  return (
    s === AgentEventRunStatus.Finished ||
    s === AgentEventRunStatus.Error ||
    s === AgentEventRunStatus.Cancelled ||
    s === AgentEventRunStatus.Expired
  )
}

/**
 * Project `run.wait()`'s terminal status string onto the wire enum. The SDK's
 * `RunResultStatus` is `'finished' | 'error' | 'cancelled'` (no 'expired' —
 * expiry only surfaces via the in-stream status frame).
 */
function mapWaitStatus(s: 'finished' | 'error' | 'cancelled'): AgentEventRunStatus {
  switch (s) {
    case 'finished':
      return AgentEventRunStatus.Finished
    case 'error':
      return AgentEventRunStatus.Error
    case 'cancelled':
      return AgentEventRunStatus.Cancelled
  }
}

/**
 * Build the wire `RunResultPayload` from the Cursor SDK's `run.wait()` result
 * plus a best-effort `agent.listArtifacts()` call. Pinned to the contract in
 * cursor-native-chat-ux card 3 (RunResultPayload required fields):
 *
 *   - durationMs: from `runResult.durationMs`; 0 if SDK didn't surface it.
 *   - model: from `runResult.model.id` if shape matches; else fallback to the
 *     factory's resolved model id (we always have one — see model resolution).
 *   - gitBranch: first non-empty `runResult.git.branches[*].branch`.
 *   - gitPrUrl: first non-empty `runResult.git.branches[*].prUrl`.
 *   - artifacts: every entry from `agent.listArtifacts()` projected to
 *     `RunArtifactPayload`. Errors yield an empty array (the terminal status
 *     still ships — a missing artifact list shouldn't block run completion).
 */
async function buildRunResultPayload(args: {
  runResult: CursorRunResult
  agent: CursorSdkAgent
  fallbackModelId: string
  logger: Logger
}): Promise<RunResultPayload> {
  const { runResult, agent, fallbackModelId, logger } = args

  // Model: SDK uses ModelSelection { id, params? } on wait result; fall
  // back to the factory's resolved id when SDK didn't echo it.
  let modelId = fallbackModelId
  const rm = runResult.model
  if (typeof rm === 'string' && rm !== '') {
    modelId = rm
  } else if (rm !== null && typeof rm === 'object' && typeof rm.id === 'string' && rm.id !== '') {
    modelId = rm.id
  }

  // Git: first branch with a non-empty branch/prUrl wins. The SDK returns
  // `branches[]` (potentially multiple repos in a future cloud case); for
  // local agents today this is at most one entry.
  let gitBranch: string | undefined
  let gitPrUrl: string | undefined
  const branches = runResult.git?.branches
  if (Array.isArray(branches)) {
    for (const b of branches) {
      if (
        gitBranch === undefined &&
        typeof b?.branch === 'string' &&
        b.branch !== ''
      ) {
        gitBranch = b.branch
      }
      if (
        gitPrUrl === undefined &&
        typeof b?.prUrl === 'string' &&
        b.prUrl !== ''
      ) {
        gitPrUrl = b.prUrl
      }
      if (gitBranch !== undefined && gitPrUrl !== undefined) break
    }
  }

  // Artifacts: best-effort. The SDK adds this surface in newer versions; an
  // older SDK / cloud agent may not implement it. A throw / undefined here
  // produces an empty artifact list rather than killing the terminal status.
  let artifacts: RunArtifactPayload[] = []
  if (typeof agent.listArtifacts === 'function') {
    try {
      const list = await agent.listArtifacts()
      if (Array.isArray(list)) {
        artifacts = list.map((a) => {
          // Tolerate either a Date or ISO string from the SDK; the wire
          // contract is `Date | string`, both encode the same JSON shape.
          const updatedAt: Date | string =
            typeof a.updatedAt === 'string'
              ? a.updatedAt
              : new Date().toISOString()
          return {
            path: typeof a.path === 'string' ? a.path : '',
            sizeBytes: typeof a.sizeBytes === 'number' ? a.sizeBytes : 0,
            updatedAt,
          }
        })
      }
    } catch (err) {
      logger.warn(
        { err, agentId: agent.agentId },
        '[cursor] agent.listArtifacts() failed — emitting empty artifact list',
      )
    }
  }

  const payload: RunResultPayload = {
    durationMs:
      typeof runResult.durationMs === 'number' ? runResult.durationMs : 0,
    model: modelId,
    artifacts,
  }
  if (gitBranch !== undefined) payload.gitBranch = gitBranch
  if (gitPrUrl !== undefined) payload.gitPrUrl = gitPrUrl
  return payload
}

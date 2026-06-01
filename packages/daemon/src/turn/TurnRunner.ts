// TurnRunner — orchestrates Cursor agent turns over SignalR.

import type { Logger } from 'pino'
import { EventEmitter } from 'node:events'
import { randomUUID } from 'node:crypto'

import type { DaemonConfig } from '../config/DaemonConfig.js'
import type { GitModule } from '../git/GitModule.js'
import type { SignalRClient } from '../signalr/SignalRClient.js'
import type {
  AgentSecretsDto,
  CancelTurnPayload,
  EmitEventPayload,
  StartTurnPayload,
  TurnCompletedPayload,
  TurnRefusedPayload,
} from '../signalr/types.js'
import { AgentEventKind, AgentEventRunStatus } from '../signalr/types.js'

import type { AgentFactory } from './AgentFactory.js'
import type { TurnEvent } from './TurnEvent.js'
import type { TurnOptions } from './TurnOptions.js'
import { BoundedAsyncQueue } from './BoundedAsyncQueue.js'
import type { AfterPromptHook, CustomTool } from './types.js'
import {
  DAEMON_TOOLS_MCP_PORT,
  DAEMON_TOOLS_SERVER_NAME,
  type DaemonToolsMcpServer,
} from '../mcp/DaemonToolsMcpServer.js'

const REPO_DIR = '/data/project/repo' as const
const DAEMON_TOOLS_MCP_URL = `http://127.0.0.1:${DAEMON_TOOLS_MCP_PORT}` as const

/**
 * Per-turn mapping context threaded through the per-event loop.
 *
 *   - `sdkSessionId` is the SDK-assigned agent id, captured from the first
 *     event that carries one. TurnRunner echoes it back to the hub as the
 *     `newAgentId` resume hint on the terminal Status envelope.
 *   - `didEmitVisibleContent` flips true whenever the mapper yields an event
 *     that counts as visible turn content (AssistantText / Thinking / ToolUse).
 *     A turn whose iterator closes with this flag still false produced
 *     nothing the user can see — TurnRunner marks it Failed with reason
 *     "empty_response" rather than the silent Succeeded that used to land
 *     in the DB. System / Status / PromptReceived / Task events deliberately
 *     do NOT count: they're plumbing/echo, not assistant content.
 */
type MapperContext = {
  sdkSessionId?: string
  didEmitVisibleContent?: boolean
}

export type TurnState =
  | { kind: 'idle' }
  | {
      kind: 'running'
      sessionId: string
      conversationId: string
      turnId: string
      agentId?: string
      skipHooks: boolean
      startedAt: Date
      abort: AbortController
    }
  | { kind: 'canceling'; sessionId: string; turnId: string }

export interface TurnIdlePayload {
  sessionId: string
  conversationId: string
  turnId: string
  /** Cursor agent id — forwarded to self-heal hooks under this legacy field name. */
  agentId: string
  skipHooks: boolean
  userPrompt: string
}

type TurnRunnerEvents = {
  idle: [TurnIdlePayload]
  activity: []
}

interface TurnRunnerDeps {
  signalr: SignalRClient
  config: DaemonConfig
  cursorFactory: AgentFactory
  customTools?: readonly CustomTool[]
  daemonToolsMcpServer: DaemonToolsMcpServer
  afterPromptHooks?: readonly AfterPromptHook[]
  gitModule?: GitModule
  logger: Logger
}

export class TurnRunner extends EventEmitter<TurnRunnerEvents> {
  readonly #signalr: SignalRClient
  readonly #config: DaemonConfig
  readonly #cursorFactory: AgentFactory
  readonly #customTools: readonly CustomTool[]
  readonly #daemonToolsMcpServer: DaemonToolsMcpServer
  readonly #afterPromptHooks: readonly AfterPromptHook[]
  readonly #logger: Logger

  #gitModule: GitModule | undefined
  #state: TurnState = { kind: 'idle' }
  #acceptingNewTurns = true

  constructor(deps: TurnRunnerDeps) {
    super()
    this.#signalr = deps.signalr
    this.#config = deps.config
    this.#cursorFactory = deps.cursorFactory
    this.#customTools = deps.customTools ?? []
    this.#daemonToolsMcpServer = deps.daemonToolsMcpServer
    this.#afterPromptHooks = deps.afterPromptHooks ?? []
    this.#gitModule = deps.gitModule
    this.#logger = deps.logger.child({ module: 'turn-runner' })
  }

  setGitModule(gitModule: GitModule): void {
    this.#gitModule = gitModule
  }

  start(): void {
    this.#signalr.onStartTurn((p) => {
      void this.#handleStartTurn(p).catch((err) => {
        this.#logger.error(
          { err, sessionId: p.sessionId },
          'unhandled error in #handleStartTurn — emitting daemon_unhandled_error',
        )
        this.#state = { kind: 'idle' }
        // Recovery: emit a Status/Error event carrying the unhandled error
        // semantic in eventData. The hub's AgentEventRunStatus.Error case
        // calls Session.Fail() and TryRecoverStaleAgentIdAsync.
        const recovery: EmitEventPayload = {
          sessionId: p.sessionId,
          kind: AgentEventKind.Status,
          runStatus: AgentEventRunStatus.Error,
          statusMessage: err instanceof Error ? err.message : String(err),
          eventData: JSON.stringify({
            type: 'daemon_unhandled_error',
            reason: 'daemon_unhandled_error',
            message: err instanceof Error ? err.message : String(err),
          }),
          emittedAt: new Date().toISOString(),
        }
        void this.#signalr.emitEvent(recovery).catch((emitErr) => {
          this.#logger.error(
            { err: emitErr, sessionId: p.sessionId },
            'failed to emit daemon_unhandled_error Status; server-side timeout will sweep',
          )
        })
      })
    })
    this.#signalr.onCancelTurn((p) => {
      void this.#handleCancelTurn(p).catch((err) => {
        this.#logger.error(
          { err, sessionId: p.sessionId },
          'unhandled error in #handleCancelTurn — logged and swallowed',
        )
      })
    })
  }

  async stop(): Promise<void> {
    this.#acceptingNewTurns = false
    if (this.#state.kind === 'running') {
      await this.cancel('shutdown')
    }
  }

  state(): TurnState {
    return this.#state
  }

  getActiveTurn(): { conversationId: string; turnId: string } | null {
    if (this.#state.kind !== 'running') return null
    return { conversationId: this.#state.conversationId, turnId: this.#state.turnId }
  }

  setAcceptingNewTurns(accepting: boolean): void {
    this.#acceptingNewTurns = accepting
  }

  async cancel(_reason: string): Promise<void> {
    if (this.#state.kind !== 'running') return
    const { sessionId, turnId, abort } = this.#state
    this.#state = { kind: 'canceling', sessionId, turnId }
    abort.abort()
  }

  async #handleStartTurn(p: StartTurnPayload): Promise<void> {
    if (p.runtimeId !== undefined && p.runtimeId !== this.#config.runtimeId) {
      this.#logger.warn(
        { got: p.runtimeId, expected: this.#config.runtimeId },
        'StartTurn dropped: runtimeId mismatch (second-line)',
      )
      return
    }

    if (!this.#acceptingNewTurns) {
      await this.#emitTurnRejected(p, 'daemon_draining')
      return
    }

    if (this.#state.kind === 'running' || this.#state.kind === 'canceling') {
      const currentSessionId = this.#state.sessionId
      const refused: TurnRefusedPayload = {
        sessionId: p.sessionId,
        reason: 'turn_already_running',
        currentSessionId,
      }
      this.#logger.warn(
        { sessionId: p.sessionId, reason: 'turn_already_running', currentSessionId },
        'StartTurn refused: another turn is already in flight',
      )
      try {
        await this.#signalr.invoke('TurnRefused', refused)
      } catch (err) {
        this.#logger.error(
          { err, sessionId: p.sessionId, currentSessionId },
          'failed to invoke TurnRefused',
        )
      }
      return
    }

    const maybeTurnId = (p as unknown as { turnId?: unknown }).turnId
    const turnId = typeof maybeTurnId === 'string' ? maybeTurnId : randomUUID()
    const skipHooks = (p as unknown as { skipHooks?: unknown }).skipHooks === true

    const abort = new AbortController()
    this.#state = {
      kind: 'running',
      sessionId: p.sessionId,
      conversationId: p.conversationId,
      turnId,
      skipHooks,
      startedAt: new Date(),
      abort,
    }
    this.emit('activity')

    let agentId: string | undefined =
      typeof p.agentId === 'string' && p.agentId !== '' ? p.agentId : undefined
    let success = false
    let failureReason: string | undefined
    let error: string | undefined

    try {
      const secrets = await this.#signalr.getSecrets()
      const hasCursorKey =
        typeof secrets.cursorApiKey === 'string' && secrets.cursorApiKey !== ''
      this.#logger.info(
        { sessionId: p.sessionId, turnId, hasCursorKey },
        'secrets.received',
      )

      if (!hasCursorKey) {
        this.#logger.warn(
          { sessionId: p.sessionId, turnId },
          'no_credentials: GetSecrets returned no Cursor API key — refusing turn',
        )
        const noCredsPayload: EmitEventPayload = {
          sessionId: p.sessionId,
          kind: AgentEventKind.Status,
          runStatus: AgentEventRunStatus.Error,
          statusMessage: 'No Cursor API key configured for this project.',
          eventData: JSON.stringify({
            type: 'no_credentials',
            reason: 'no_credentials',
            message: 'No Cursor API key configured for this project.',
          }),
          emittedAt: new Date().toISOString(),
        }
        try {
          await this.#signalr.emitEvent(noCredsPayload)
        } catch (emitErr) {
          this.#logger.error(
            { err: emitErr, sessionId: p.sessionId, turnId },
            'failed to emit Status/no_credentials event',
          )
        }
        failureReason = 'no_credentials'
        const sentinel: Error & { __noCredentials?: true } = new Error('no_credentials')
        sentinel.__noCredentials = true
        throw sentinel
      }

      if (p.pullBeforeStart) {
        if (this.#gitModule === undefined) {
          this.#logger.warn(
            { sessionId: p.sessionId, turnId },
            'pullBeforeStart=true but no gitModule wired — skipping FF pull',
          )
        } else {
          let branch = ''
          let pullErrorMessage: string | undefined
          try {
            branch = await this.#gitModule.currentBranch()
            await this.#gitModule.pullFastForward(branch, {
              conversationId: p.conversationId,
              turnId,
            })
          } catch (pullErr) {
            pullErrorMessage =
              pullErr instanceof Error ? pullErr.message : String(pullErr)
            this.#logger.warn(
              { err: pullErr, sessionId: p.sessionId, turnId, branch },
              'FF pull failed — emitting branch_divergent',
            )
          }

          if (pullErrorMessage !== undefined) {
            const divergentPayload: EmitEventPayload = {
              sessionId: p.sessionId,
              kind: AgentEventKind.Status,
              runStatus: AgentEventRunStatus.Error,
              statusMessage: pullErrorMessage,
              eventData: JSON.stringify({
                type: 'branch_divergent',
                reason: 'branch_divergent',
                message: pullErrorMessage,
              }),
              emittedAt: new Date().toISOString(),
            }
            try {
              await this.#signalr.emitEvent(divergentPayload)
            } catch (emitErr) {
              this.#logger.error(
                { err: emitErr, sessionId: p.sessionId, turnId, branch },
                'failed to emit Status/branch_divergent event',
              )
            }
            failureReason = 'branch_divergent'
            const sentinel: Error & { __branchDivergent?: true } = new Error(
              'branch_divergent',
            )
            sentinel.__branchDivergent = true
            throw sentinel
          }
        }
      }

      const toolContext = {
        signalr: this.#signalr,
        config: this.#config,
        sessionId: p.sessionId,
        turnId,
      }
      this.#daemonToolsMcpServer.setTurnContext(toolContext)
      try {
        const stream = this.#cursorFactory(
          this.#buildTurnOptions(p, turnId, abort.signal, secrets),
        )
        const mapperCtx: MapperContext = { didEmitVisibleContent: false }
        let sawSyntheticError: { subtype: string; error: string } | undefined
        let sawTerminalErrorStatus:
          | { runStatus: AgentEventRunStatus; message?: string }
          | undefined

        const emitQueue = new BoundedAsyncQueue<TurnEvent>(100)
        const consumerTask = (async () => {
          for await (const ev of emitQueue) {
            await this.#emitMappedEvent(p.sessionId, ev, mapperCtx)
          }
        })()

        try {
          for await (const event of stream) {
            // Capture the SDK agent id from the first event that carries one
            // (mapper sets `agentId` on every event that has frame.agent_id).
            if (
              typeof event.agentId === 'string' &&
              event.agentId !== '' &&
              agentId === undefined
            ) {
              agentId = event.agentId
            }
            // Synthetic error breadcrumb from the factory's catch path —
            // CursorFactory emits a System carrier with subtype 'error' or
            // 'cursor_parse_error' before re-throwing.
            if (
              event.kind === 'System' &&
              sawSyntheticError === undefined &&
              (event.subtype === 'error' || event.subtype === 'cursor_parse_error')
            ) {
              const data = event.eventData as { error?: unknown } | null
              const errMsg =
                data !== null && typeof data === 'object' && typeof data.error === 'string'
                  ? data.error
                  : JSON.stringify(data ?? null)
              sawSyntheticError = { subtype: event.subtype, error: errMsg }
            }
            // Terminal Status frame from Cursor (FINISHED/ERROR/CANCELLED/EXPIRED).
            // If the run reports a failure mode, capture so the post-loop
            // success/failure logic stamps the right reason on the envelope.
            if (event.kind === AgentEventKind.Status) {
              const rs = event.runStatus
              const isFailure =
                rs === AgentEventRunStatus.Error ||
                rs === AgentEventRunStatus.Cancelled ||
                rs === AgentEventRunStatus.Expired
              if (isFailure) {
                sawTerminalErrorStatus = {
                  runStatus: rs,
                  ...(typeof event.statusMessage === 'string' && event.statusMessage !== ''
                    ? { message: event.statusMessage }
                    : {}),
                }
              }
            }
            await emitQueue.push(event)
          }
        } finally {
          emitQueue.close()
          await consumerTask
        }

        if (sawSyntheticError !== undefined) {
          failureReason = sawSyntheticError.subtype
          error = sawSyntheticError.error
        } else if (sawTerminalErrorStatus !== undefined) {
          failureReason =
            sawTerminalErrorStatus.runStatus === AgentEventRunStatus.Cancelled
              ? 'canceled'
              : sawTerminalErrorStatus.runStatus === AgentEventRunStatus.Expired
                ? 'expired'
                : 'error'
          error =
            sawTerminalErrorStatus.message ?? 'agent run ended with a terminal error status'
        } else if (mapperCtx.didEmitVisibleContent === true) {
          success = true
        } else {
          failureReason = 'empty_response'
          error = 'upstream model returned no content'
        }
      } finally {
        this.#daemonToolsMcpServer.setTurnContext(null)
      }
    } catch (err) {
      const noCreds =
        err !== null &&
        typeof err === 'object' &&
        (err as { __noCredentials?: unknown }).__noCredentials === true
      const branchDivergent =
        err !== null &&
        typeof err === 'object' &&
        (err as { __branchDivergent?: unknown }).__branchDivergent === true
      if (!noCreds && !branchDivergent) {
        const isAbort =
          (err instanceof Error && err.name === 'AbortError') || abort.signal.aborted
        if (isAbort) {
          failureReason = 'canceled'
        } else {
          failureReason = 'sdk_error'
          error = err instanceof Error ? err.message : String(err)
          this.#logger.error(
            { err, sessionId: p.sessionId, turnId },
            'Cursor SDK threw mid-turn',
          )
        }
      }
    }

    for (const hook of this.#afterPromptHooks) {
      try {
        const ctx: { sessionId: string; turnId: string; agentId?: string } = {
          sessionId: p.sessionId,
          turnId,
        }
        if (agentId !== undefined) ctx.agentId = agentId
        await hook(ctx)
      } catch (err) {
        this.#logger.error(
          { err, sessionId: p.sessionId, turnId },
          'afterPromptHook threw; continuing',
        )
      }
    }

    // The runner's authoritative turn-completion envelope. Carries the
    // resume hint (`newAgentId`) so the hub's tolerant `newAgentId` /
    // `newCursorAgentId` JSON probe in RuntimeHub.EmitEvent picks it up. The
    // hub's status state-machine fired on the mapper's terminal Status frame
    // already; this envelope's RunStatus is informational — Finished on
    // success, Error on failure — driving the AgentEvents audit row but not
    // re-firing the session lifecycle (the hub treats `Finished` while the
    // session is already Succeeded as a no-op).
    const completedPayload: TurnCompletedPayload = {
      runtimeId: this.#config.runtimeId,
      sessionId: p.sessionId,
      turnId,
      success,
      ...(failureReason !== undefined ? { reason: failureReason } : {}),
      ...(agentId !== undefined ? { newAgentId: agentId } : {}),
      ...(error !== undefined ? { error } : {}),
    }
    const completedEnvelope: EmitEventPayload = {
      sessionId: p.sessionId,
      kind: AgentEventKind.Status,
      runStatus: success ? AgentEventRunStatus.Finished : AgentEventRunStatus.Error,
      ...(error !== undefined ? { statusMessage: error } : {}),
      eventData: JSON.stringify(completedPayload),
      emittedAt: new Date().toISOString(),
    }
    try {
      await this.#signalr.emitEvent(completedEnvelope)
    } catch (err) {
      this.#logger.error(
        { err, sessionId: p.sessionId, turnId },
        'failed to emit terminal Status envelope',
      )
    }

    this.#state = { kind: 'idle' }
    this.emit('idle', {
      sessionId: p.sessionId,
      conversationId: p.conversationId,
      turnId,
      agentId: agentId ?? '',
      skipHooks,
      userPrompt: p.prompt ?? '',
    })
  }

  async #handleCancelTurn(p: CancelTurnPayload): Promise<void> {
    if (this.#state.kind === 'idle') return
    if (this.#state.sessionId !== p.sessionId) {
      this.#logger.debug(
        { got: p.sessionId, current: this.#state.sessionId },
        'CancelTurn for non-current session — ignoring',
      )
      return
    }
    await this.cancel(p.reason)
  }

  #buildTurnOptions(
    p: StartTurnPayload,
    turnId: string,
    signal: AbortSignal,
    secrets: AgentSecretsDto,
  ): TurnOptions {
    const maybeModel = (p as unknown as { model?: unknown }).model
    const model = typeof maybeModel === 'string' ? maybeModel : undefined
    const mcpUrlsRaw = (p as unknown as { mcpUrls?: unknown }).mcpUrls
    const mcpUrls = Array.isArray(mcpUrlsRaw)
      ? mcpUrlsRaw.filter((u): u is string => typeof u === 'string')
      : []

    const opts: TurnOptions = {
      prompt: p.prompt,
      cwd: REPO_DIR,
      abortSignal: signal,
      secrets,
    }

    const resumeId = p.agentId
    if (resumeId !== null && resumeId !== undefined && resumeId !== '') {
      opts.resume = resumeId
    }
    if (model !== undefined) opts.model = model

    const mcpServers: Record<string, unknown> = {}
    if (mcpUrls.length > 0) Object.assign(mcpServers, this.#buildMcpServers(mcpUrls))

    if (this.#customTools.length > 0) {
      mcpServers[DAEMON_TOOLS_SERVER_NAME] = {
        type: 'http',
        url: DAEMON_TOOLS_MCP_URL,
      }
    }

    if (Object.keys(mcpServers).length > 0) opts.mcpServers = mcpServers
    return opts
  }

  #buildMcpServers(mcpUrls: readonly string[]): Record<string, unknown> {
    const map: Record<string, unknown> = {}
    for (const url of mcpUrls) {
      let name: string
      try {
        const parsed = new URL(url)
        name =
          parsed.pathname.split('/').filter(Boolean).pop() ??
          parsed.hostname ??
          'mcp'
      } catch {
        name = 'mcp'
      }
      map[name] = {
        type: 'http',
        url,
        headers: { Authorization: `Bearer ${this.#config.runtimeToken}` },
      }
    }
    return map
  }

  /**
   * Translate one `MappedCursorEvent` from the mapper into the .NET
   * `EmitEventPayload` shape and ship it. Pure projection — the mapper
   * already picked the `kind` discriminator and populated the per-kind
   * first-class fields.
   *
   * Visible-content tracking: AssistantText / Thinking / ToolUse all count
   * as "user-facing turn content" — a turn with none of these counts as
   * empty_response. PromptReceived and Task and Status are run-level
   * surfaces and don't flip the flag.
   */
  async #emitMappedEvent(
    sessionId: string,
    event: TurnEvent,
    ctx: MapperContext,
  ): Promise<void> {
    // Capture sdkSessionId mirror for parity with the legacy MapperContext
    // surface — TurnRunner's own bookkeeping uses agentId directly, but
    // tests / future surfaces may read this.
    if (
      typeof event.agentId === 'string' &&
      event.agentId !== '' &&
      ctx.sdkSessionId === undefined
    ) {
      ctx.sdkSessionId = event.agentId
    }

    if (
      event.kind === AgentEventKind.AssistantText ||
      event.kind === AgentEventKind.Thinking ||
      event.kind === AgentEventKind.ToolUse
    ) {
      ctx.didEmitVisibleContent = true
    }

    const payload = this.#buildEmitPayload(sessionId, event)
    if (payload === null) return
    try {
      await this.#signalr.emitEvent(payload)
    } catch (err) {
      this.#logger.error(
        { err, sessionId, kind: event.kind },
        'failed to emit wire event',
      )
    }
  }

  /**
   * Project a `MappedCursorEvent` into the .NET `EmitEventPayload` shape.
   * Returns `null` if the event should be silently dropped (no current
   * cases, but kept as an escape hatch for forward compatibility).
   */
  #buildEmitPayload(sessionId: string, event: TurnEvent): EmitEventPayload | null {
    const base = {
      sessionId,
      emittedAt: new Date().toISOString(),
    }

    switch (event.kind) {
      case AgentEventKind.PromptReceived:
        return {
          ...base,
          kind: AgentEventKind.PromptReceived,
          text: event.text,
          eventData: '{}',
        }
      case AgentEventKind.AssistantText:
        return {
          ...base,
          kind: AgentEventKind.AssistantText,
          text: event.text,
          eventData: '{}',
        }
      case AgentEventKind.Thinking: {
        const p: EmitEventPayload = {
          ...base,
          kind: AgentEventKind.Thinking,
          text: event.text,
          eventData: '{}',
        }
        if (event.thinkingDurationMs !== undefined) {
          p.thinkingDurationMs = event.thinkingDurationMs
        }
        return p
      }
      case AgentEventKind.ToolUse: {
        const p: EmitEventPayload = {
          ...base,
          kind: AgentEventKind.ToolUse,
          toolCallId: event.toolCallId,
          toolName: event.toolName,
          toolStatus: event.toolStatus,
          eventData: '{}',
        }
        if (event.toolArgs !== undefined) p.toolArgs = event.toolArgs
        if (event.toolResult !== undefined) p.toolResult = event.toolResult
        if (event.toolArgsTruncated !== undefined) {
          p.toolArgsTruncated = event.toolArgsTruncated
        }
        if (event.toolResultTruncated !== undefined) {
          p.toolResultTruncated = event.toolResultTruncated
        }
        return p
      }
      case AgentEventKind.Status: {
        const p: EmitEventPayload = {
          ...base,
          kind: AgentEventKind.Status,
          runStatus: event.runStatus,
          eventData: '{}',
        }
        if (event.statusMessage !== undefined) {
          p.statusMessage = event.statusMessage
        }
        if (event.runResult !== undefined) {
          p.runResult = event.runResult
        }
        return p
      }
      case AgentEventKind.Task: {
        const p: EmitEventPayload = {
          ...base,
          kind: AgentEventKind.Task,
          taskId: event.taskId,
          eventData: '{}',
        }
        if (event.taskTitle !== undefined) p.taskTitle = event.taskTitle
        return p
      }
      case 'System': {
        // System carriers ride on AgentEventKind.Status — the .NET hub's
        // RunStatus column stays null (we don't set it) so the session
        // state-machine doesn't fire. The audit row keeps the opaque
        // eventData for forensic replay.
        return {
          ...base,
          kind: AgentEventKind.Status,
          eventData: JSON.stringify(event.eventData),
        }
      }
      default: {
        // Forward-compat: a future mapper output kind we don't know about.
        // Soft-drop with a log so production telemetry surfaces the gap.
        this.#logger.warn(
          { kind: (event as { kind?: unknown }).kind },
          'unknown mapped cursor event kind — dropping',
        )
        return null
      }
    }
  }

  async #emitTurnRejected(p: StartTurnPayload, reason: string): Promise<void> {
    const payload: EmitEventPayload = {
      sessionId: p.sessionId,
      kind: AgentEventKind.Status,
      runStatus: AgentEventRunStatus.Error,
      statusMessage: reason,
      eventData: JSON.stringify({ type: 'turn_rejected', reason }),
      emittedAt: new Date().toISOString(),
    }
    try {
      await this.#signalr.emitEvent(payload)
    } catch (err) {
      this.#logger.error(
        { err, sessionId: p.sessionId, reason },
        'failed to emit turn_rejected',
      )
    }
  }
}

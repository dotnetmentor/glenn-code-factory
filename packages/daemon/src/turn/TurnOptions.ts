import type { AgentSecretsDto } from '../signalr/types.js'

/**
 * Per-turn options passed from TurnRunner into an `AgentFactory`
 * (CursorFactory or ClaudeFactory).
 */
export type TurnOptions = {
  prompt: string
  resume?: string
  model?: string
  cwd: string
  abortSignal?: AbortSignal
  mcpServers?: Record<string, unknown>
  secrets?: AgentSecretsDto
  /**
   * Which agent backend should service this turn. Defaults to `cursor` at the
   * selection point (TurnRunner) when the StartTurn payload omits it. Read by
   * TurnRunner to pick the factory; not consumed by the factories themselves.
   */
  backend?: 'cursor' | 'claude'
  /**
   * Reasoning effort for the Claude backend (SDK `effort`). Ignored by the
   * Cursor backend. Maps directly to the SDK's `EffortLevel`.
   */
  reasoningEffort?: 'low' | 'medium' | 'high' | 'xhigh' | 'max'
  /**
   * Permission posture. When `true` the Claude backend uses
   * `permissionMode: 'bypassPermissions'` (full autonomy); otherwise
   * `acceptEdits`. The Cursor backend ignores this today.
   */
  yolo?: boolean
}

/** @deprecated Use TurnOptions */
export type SdkQueryOptions = TurnOptions

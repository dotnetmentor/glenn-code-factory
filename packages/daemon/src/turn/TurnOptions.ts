import type { AgentSecretsDto } from '../signalr/types.js'

/**
 * Per-turn options passed from TurnRunner into CursorFactory.
 */
export type TurnOptions = {
  prompt: string
  resume?: string
  model?: string
  cwd: string
  abortSignal?: AbortSignal
  mcpServers?: Record<string, unknown>
  secrets?: AgentSecretsDto
}

/** @deprecated Use TurnOptions */
export type SdkQueryOptions = TurnOptions

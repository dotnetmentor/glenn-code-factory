// Stub shapes for custom tools + after-prompt hooks.
//
// The actual implementations of these land in sibling cards:
//   - CustomTool concrete implementations land in the daemon `Custom tools` card
//     (`propose_runtime_spec`, `restart_service`).
//   - AfterPromptHook execution + spec-derived hook config lands in the
//     `daemon-hooks-runner` spec.
//
// We declare the *shapes* here so TurnRunner can accept them as injected
// dependencies today, and the future cards drop in implementations without
// touching TurnRunner.
//
// Both are deliberately minimal: a plain `type` for the hook (it's just a
// function), a `type` (not interface) for the tool because callers compose
// these as object literals — no inheritance hierarchy needed.

import type { SignalRClient } from '../signalr/SignalRClient.js'
import type { DaemonConfig } from '../config/DaemonConfig.js'

/**
 * Context handed to a CustomTool's `run` method. Keeps tools decoupled from
 * the orchestrator — a tool takes only what it needs through this single
 * argument.
 */
export type ToolContext = {
  signalr: SignalRClient
  config: DaemonConfig
  sessionId: string
  turnId: string
}

/**
 * Result of running a tool. Open shape — the SDK wraps whatever we return into
 * its own tool-result envelope. The Custom Tools card will narrow this if a
 * stronger shape proves useful.
 */
export type ToolResult = unknown

/**
 * A platform-defined tool exposed to the Cursor SDK on every turn via
 * DaemonToolsMcpServer. The SDK calls `run(args, ctx)` when the model invokes the tool.
 *
 * `inputSchema` is typed as `object` (rather than a JsonSchema-specific shape)
 * until we wire up Ajv or zod in the Custom Tools card — the SDK accepts a
 * plain JsonSchema object.
 */
export type CustomTool = {
  name: string
  description: string
  inputSchema: object
  run(args: unknown, ctx: ToolContext): Promise<ToolResult>
}

/**
 * After-prompt hook. Runs once per completed turn (success OR failure), in
 * registration order, best-effort — a throwing hook is logged and the next
 * one was captured during the turn, so resume-aware hooks can persist it.
 *
 * Concrete hook implementations land in the `daemon-hooks-runner` spec.
 */
export type AfterPromptHook = (ctx: {
  sessionId: string
  turnId: string
  agentId?: string
}) => Promise<void>

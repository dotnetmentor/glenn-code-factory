// McpRegistry — Spec 15 Card 5.
//
// In-memory snapshot of the MCP servers the project's spec scopes to this
// runtime. Populated once at boot by `FetchingStage` (which fetches
// `/api/runtimes/{runtimeId}/bootstrap-mcp-config`) and read on every turn by
// `TurnRunner`/`CursorFactory` so the Cursor SDK can call them.
//
// Unlike `EnvVarManager`, MCPs aren't persisted to disk — they're consumed
// purely in-process. No filesystem, no atomic-rewrite dance. A simple readonly
// array behind a defensive copy is the right shape.
//
// === Why a class (not a plain object) ===
//
// Two reasons:
//   1. Defensive copy on `loadInitial`/`replaceAll` — callers can mutate their
//      input array post-call without aliasing internal state.
//   2. Future hook for live updates: when the live `UpdateConfig` MCP delta
//      path lands (currently out-of-scope per spec) it'll be a method on this
//      class (`applyDelta`, `addServer`, …) without touching the read path.
//
// === Why no logger / events ===
//
// The bootstrap stage logs the count once at load. Reads happen on the per-turn
// hot path; we don't log every read. There's nothing to listen for either —
// `replaceAll` isn't called yet, so no observer surface is justified today.

/**
 * One MCP server entry as scoped to this runtime by the project's RuntimeSpec.
 *
 * `baseUrl` is the SSE endpoint the SDK connects to (the backend's
 * `McpControllerBase` lives there, scoped by the JWT bearer claim). `name` is
 * the tool-prefix the SDK uses in calls (e.g. `mcp__github__create_issue`);
 * `version` is reported in the MCP `initialize` handshake but doesn't affect
 * routing today.
 */
export type McpEntry = {
  readonly name: string
  readonly version: string
  readonly baseUrl: string
}

export class McpRegistry {
  #entries: readonly McpEntry[] = []

  /**
   * Bootstrap-time replace. Called once by `FetchingStage` after the HTTP
   * fetch lands. Defensive-copies the input so a mutating caller doesn't
   * silently change our snapshot.
   */
  loadInitial(entries: readonly McpEntry[]): void {
    this.#entries = [...entries]
  }

  /**
   * Snapshot of the current MCP entries. Returned as a `readonly` array — the
   * caller can iterate but cannot push/splice. We hand back the live reference
   * (typed readonly) since every mutation goes through `loadInitial` /
   * `replaceAll` and we never expose write access.
   */
  entries(): readonly McpEntry[] {
    return this.#entries
  }

  /**
   * Forward-compat hook for the live-update path (out of scope for this card,
   * but the slot is reserved). Same defensive-copy semantics as `loadInitial`.
   */
  replaceAll(entries: readonly McpEntry[]): void {
    this.#entries = [...entries]
  }
}

import { useEffect, useMemo, useState } from 'react'
import { HubConnectionState } from '@microsoft/signalr'
import {
  acquireAgentHub,
  releaseAgentHub,
  type AgentHubConnection,
} from './agentHub'

/**
 * Options for {@link useAgentHub}.
 */
export interface UseAgentHubOptions {
  /**
   * Project id forwarded to the AgentHub query string. Used for the
   * wake-on-connect path on the backend (project-keyed runtime wake). May
   * be omitted when only the workspace sidebar wires the hook.
   */
  projectId?: string
  /**
   * Branch id forwarded to the AgentHub query string. This is what scopes
   * the connection to the matching <c>branch-{id}</c> SignalR group on the
   * backend — live AgentEvent ticks (assistant chunks, tool calls, turn
   * status) are routed per-branch so sibling-branch tabs don't see each
   * other's chat after CopyBranch. The connection rekeys on this value:
   * switching branches tears down the old socket and starts a new one.
   *
   * <p>When undefined the hook still builds a connection (for the
   * workspace-only sidebar use case) but no branch group is joined and
   * live agent events will not be delivered to this tab.</p>
   */
  branchId?: string
  /**
   * Optional workspace id. When supplied (and the connection is up) the hook
   * invokes {@code JoinWorkspace(workspaceId)} on the hub so this connection
   * also receives the workspace-scoped fan-out — runtime / agent / rename
   * events for every project in the workspace, not just the active one. The
   * sidebar uses this to keep a live status map across the whole workspace.
   *
   * <p>If the user is not a member of the workspace the hub throws and we
   * log + continue — the per-project subscription should still work.</p>
   */
  workspaceId?: string
  /**
   * Set to false to skip connecting even when a {@link projectId},
   * {@link branchId} or {@link workspaceId} is provided — useful for pages
   * that mount the hook unconditionally but gate it on a feature flag or
   * auth state.
   */
  enabled?: boolean
}

/**
 * Lazy AgentHub SignalR connection lifecycle hook.
 *
 * <p>Backed by a module-level ref-counted pool ({@link acquireAgentHub} /
 * {@link releaseAgentHub}) so multiple components mounting the hook with the
 * same {@code (projectId, branchId)} share a single underlying SignalR socket
 * and a single negotiate round-trip — historically each hook instance opened
 * its own connection, which on a workspace branch view fanned out to 6
 * concurrent negotiates (3 consumers × StrictMode's intentional double mount
 * in dev). The pool keeps the socket warm across StrictMode unmount-then-
 * remount cycles via a short grace timer on release.</p>
 *
 * <p>The hook returns the wrapper plus a reactive {@link HubConnectionState}
 * so UI can render "connecting…" or "reconnecting…" affordances without
 * polling.</p>
 */
export function useAgentHub(opts: UseAgentHubOptions = {}): {
  connection: AgentHubConnection | null
  state: HubConnectionState
} {
  const { projectId, branchId, workspaceId, enabled = true } = opts
  const [state, setState] = useState<HubConnectionState>(
    HubConnectionState.Disconnected,
  )

  // Stable key for the pool acquire — same key string means same underlying
  // socket. Acquire is synchronous (pool hit or fresh build); start happens
  // inside acquireAgentHub for the first acquirer.
  const acquireOpts = useMemo(
    () => ({ projectId, branchId }),
    [projectId, branchId],
  )

  const connection = useMemo<AgentHubConnection | null>(() => {
    if (!enabled) return null
    return acquireAgentHub(acquireOpts)
    // The pool keys on the JSON-stringified opts; using acquireOpts as the
    // memo dep ensures we re-acquire when either id changes.
  }, [enabled, acquireOpts])

  // ── Effect 1: subscribe to connection state + release on unmount ─────────
  useEffect(() => {
    if (!enabled || !connection) {
      setState(HubConnectionState.Disconnected)
      return
    }
    setState(connection.state)
    const unsubscribeState = connection.subscribeState((s) => setState(s))
    return () => {
      unsubscribeState()
      releaseAgentHub(acquireOpts)
    }
  }, [enabled, connection, acquireOpts])

  // ── Effect 2: workspace-group membership, keyed by workspaceId ───────────
  // Mirrors {@code JoinProject} (which the hub auto-runs from the query
  // string) but for the workspace fan-out. We wait for the connection to be
  // Connected before invoking — calling JoinWorkspace while the socket is
  // still Connecting throws "InvalidOperationException: The 'invoke' method
  // cannot be called if the connection is not in the 'Connected' state."
  //
  // On unmount or workspaceId change, leave the previous workspace group so
  // the connection doesn't keep receiving its broadcasts after navigating
  // out of the workspace.
  useEffect(() => {
    if (!enabled) return
    if (!connection) return
    if (!workspaceId) return
    if (state !== HubConnectionState.Connected) return

    let cancelled = false
    connection.raw
      .invoke('JoinWorkspace', workspaceId)
      .catch((err: unknown) => {
        if (cancelled) return
        // 403 / not-a-member is the expected error path — keep the
        // per-project subscription alive and surface a quiet warning so
        // the user sees something in devtools if they go looking.
        // eslint-disable-next-line no-console
        console.warn(
          `[useAgentHub] JoinWorkspace(${workspaceId}) failed:`,
          err,
        )
      })

    return () => {
      cancelled = true
      // Only attempt LeaveWorkspace if the socket is still up; once it's
      // closed the group membership is gone anyway and invoking would throw.
      if (connection.raw.state === HubConnectionState.Connected) {
        connection.raw.invoke('LeaveWorkspace', workspaceId).catch(() => {
          // best-effort — server-side group cleanup handles disconnects
        })
      }
    }
  }, [enabled, connection, workspaceId, state])

  return { connection, state }
}

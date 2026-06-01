import { useEffect, useRef } from 'react'
import {
  HubConnectionBuilder,
  HubConnectionState,
  LogLevel,
  type HubConnection,
} from '@microsoft/signalr'
import {
  getHubProxyFactory,
  getReceiverRegister,
} from '@/generated/signalr/TypedSignalR.Client'
import type {
  IPlanningClient,
  IPlanningHub,
} from '@/generated/signalr/TypedSignalR.Client/Source.Features.SignalR.Hubs'
import type {
  CardChangedNotification,
  SpecificationChangedNotification,
  SubtaskChangedNotification,
} from '@/generated/signalr/Source.Features.SignalR.Contracts'

/**
 * Subscribers a caller can pass to {@link usePlanningSignalR}. Every callback
 * is optional — the spec list / detail surface only wires
 * <c>onSpecificationChanged</c>; the kanban surface (Spec card 5) wires
 * <c>onCardChanged</c> + <c>onSubtaskChanged</c>. Both can mount the hook
 * with their own subset of callbacks and a single connection per
 * project still serves them.
 *
 * <p>The hook applies a defense-in-depth project-id filter before fanning
 * out, even though the server already groups broadcasts by
 * <c>project:{projectId}</c> — keeps a stale notification from a previous
 * project from leaking through during navigation transitions.</p>
 */
export interface PlanningSignalRCallbacks {
  onSpecificationChanged?: (n: SpecificationChangedNotification) => void
  onCardChanged?: (n: CardChangedNotification) => void
  onSubtaskChanged?: (n: SubtaskChangedNotification) => void
}

/**
 * SignalR lifecycle hook for the <c>/hubs/planning</c> endpoint.
 *
 * <p>Owns one {@link HubConnection} per hook instance — connect on mount,
 * call <c>JoinProject(projectId)</c> once Connected, fan IPlanningClient
 * notifications out to the supplied callbacks, then
 * <c>LeaveProject(projectId)</c> + stop on unmount. The connection is keyed
 * by {@code projectId} so switching projects tears down the old socket and
 * starts a new one.</p>
 *
 * <p>Callbacks are stashed in a ref so consumers can pass inline arrow
 * functions without re-establishing the socket on every render. The user
 * still gets the latest callback when an event fires.</p>
 *
 * <p>Today each caller creates its own connection (acceptable — spec list,
 * spec detail, and kanban are mutually exclusive routes). If we later mount
 * two planning surfaces at once we should hoist this into a context-backed
 * singleton.</p>
 */
export function usePlanningSignalR(
  projectId: string | undefined,
  callbacks: PlanningSignalRCallbacks,
): void {
  // Ref so the effect doesn't re-fire on every render when the consumer
  // passes inline callbacks. We still resolve the latest callback at
  // dispatch time.
  const callbacksRef = useRef<PlanningSignalRCallbacks>(callbacks)
  useEffect(() => {
    callbacksRef.current = callbacks
  }, [callbacks])

  useEffect(() => {
    if (!projectId) return

    const conn: HubConnection = new HubConnectionBuilder()
      .withUrl('/hubs/planning', { withCredentials: true })
      .withAutomaticReconnect()
      .configureLogging(LogLevel.Warning)
      .build()

    const receiver: IPlanningClient = {
      specificationChanged: async (payload) => {
        if (payload.projectId !== projectId) return
        try {
          callbacksRef.current.onSpecificationChanged?.(payload)
        } catch (err) {
          // eslint-disable-next-line no-console
          console.error('[usePlanningSignalR] onSpecificationChanged threw:', err)
        }
      },
      cardChanged: async (payload) => {
        if (payload.projectId !== projectId) return
        try {
          callbacksRef.current.onCardChanged?.(payload)
        } catch (err) {
          // eslint-disable-next-line no-console
          console.error('[usePlanningSignalR] onCardChanged threw:', err)
        }
      },
      subtaskChanged: async (payload) => {
        if (payload.projectId !== projectId) return
        try {
          callbacksRef.current.onSubtaskChanged?.(payload)
        } catch (err) {
          // eslint-disable-next-line no-console
          console.error('[usePlanningSignalR] onSubtaskChanged threw:', err)
        }
      },
    }

    const receiverDisposable = getReceiverRegister('IPlanningClient').register(
      conn,
      receiver,
    )
    const hub: IPlanningHub =
      getHubProxyFactory('IPlanningHub').createHubProxy(conn)

    let disposed = false
    let joined = false

    conn
      .start()
      .then(async () => {
        if (disposed) return
        try {
          await hub.joinProject(projectId)
          joined = true
        } catch (err) {
          // eslint-disable-next-line no-console
          console.warn(
            `[usePlanningSignalR] JoinProject(${projectId}) failed:`,
            err,
          )
        }
      })
      .catch((err: unknown) => {
        if (disposed) return
        // eslint-disable-next-line no-console
        console.warn('[usePlanningSignalR] start failed:', err)
      })

    return () => {
      disposed = true
      // Best-effort: leave the group before tearing the socket down so the
      // server-side group bookkeeping is clean even if the socket lingers.
      const cleanup = async () => {
        if (joined && conn.state === HubConnectionState.Connected) {
          try {
            await hub.leaveProject(projectId)
          } catch {
            // best-effort — server group cleanup handles disconnects
          }
        }
        try {
          receiverDisposable.dispose()
        } catch {
          // best-effort
        }
        try {
          await conn.stop()
        } catch {
          // best-effort teardown — connection might already be closing
        }
      }
      void cleanup()
    }
  }, [projectId])
}

import { useEffect } from 'react'
import { useQueryClient } from '@tanstack/react-query'
import {
  getGetApiProjectsProjectIdProposalsQueryKey,
  getGetApiProjectsProjectIdProposalsProposalIdQueryKey,
  getGetApiProjectsProjectIdRuntimeSpecQueryKey,
} from '@/api/queries-commands'
import type { AgentHubConnection } from '@/lib/signalr'

/**
 * Subscribes to <c>RuntimeProposalCreated</c> / <c>RuntimeProposalUpdated</c>
 * fan-outs on the AgentHub and invalidates the React Query caches that drive
 * the runtime workspace surface so the UI re-renders without polling.
 *
 * <p>Uses the typed {@link AgentHubConnection} wrapper rather than wiring
 * <c>connection.on('RuntimeProposalCreated', …)</c> by hand — keeps the
 * payload types tied to the generated TypedSignalR contracts.</p>
 *
 * <p>Today the AgentHub auto-joins each connection to its <c>user-{id}</c>
 * group only — there is no per-project group join helper yet (TODO in
 * <c>AgentHub.OnConnectedAsync</c>). The backend currently fans these
 * notifications to <c>project-{id}</c>, so a user with no project membership
 * row won't receive them. Once project-ownership lands, this hook is the
 * client side of that wiring; until then we still listen, so as soon as the
 * server starts adding the user to the right group the UI is already live.
 * The page-load query still fetches the initial state via REST, so the UI
 * is correct even when realtime is silent.</p>
 */
export function useProposalSignalR(
  connection: AgentHubConnection | null,
  projectId: string | undefined,
) {
  const queryClient = useQueryClient()

  useEffect(() => {
    if (!connection || !projectId) return

    const unsubCreated = connection.onRuntimeProposalCreated((payload) => {
      if (payload.projectId !== projectId) return
      queryClient.invalidateQueries({
        queryKey: getGetApiProjectsProjectIdProposalsQueryKey(projectId),
      })
    })

    const unsubUpdated = connection.onRuntimeProposalUpdated((payload) => {
      if (payload.projectId !== projectId) return
      queryClient.invalidateQueries({
        queryKey: getGetApiProjectsProjectIdProposalsQueryKey(projectId),
      })
      queryClient.invalidateQueries({
        queryKey: getGetApiProjectsProjectIdProposalsProposalIdQueryKey(
          projectId,
          payload.proposalId,
        ),
      })
      queryClient.invalidateQueries({
        queryKey: getGetApiProjectsProjectIdRuntimeSpecQueryKey(projectId),
      })
      // Runtime status moved from project-scoped to branch-scoped, so this
      // hook (super-admin runtime workspace — projectId only, no branchId in
      // scope) can't construct the exact key. The generated key is a single
      // string of shape `/api/projects/{projectId}/branches/{branchId}/runtime/status`
      // — match by prefix so every cached branch's status under this project
      // is invalidated regardless of which proposal change fired.
      const statusPrefix = `/api/projects/${projectId}/branches/`
      const statusSuffix = '/runtime/status'
      queryClient.invalidateQueries({
        predicate: (query) => {
          const first = query.queryKey[0]
          return (
            typeof first === 'string' &&
            first.startsWith(statusPrefix) &&
            first.endsWith(statusSuffix)
          )
        },
      })
    })

    return () => {
      unsubCreated()
      unsubUpdated()
    }
  }, [connection, projectId, queryClient])
}

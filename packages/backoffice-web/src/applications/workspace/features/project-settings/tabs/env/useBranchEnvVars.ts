import { useCallback } from 'react'
import { useQueryClient } from '@tanstack/react-query'
import {
  getGetApiProjectsProjectIdBranchesBranchIdEnvQueryKey,
  getGetApiProjectsProjectIdBranchesBranchIdEnvStatusQueryKey,
  useDeleteApiProjectsProjectIdBranchesBranchIdEnvKey,
  useGetApiProjectsProjectIdBranchesBranchIdEnv,
  useGetApiProjectsProjectIdBranchesBranchIdEnvStatus,
  usePostApiProjectsProjectIdBranchesBranchIdEnv,
  usePutApiProjectsProjectIdBranchesBranchIdEnvKey,
} from '../../../../../../api/queries-commands'

/**
 * Branch-scoped environment variable management.
 *
 * <p>Wraps the four generated Orval hooks (list, status, add, update, delete)
 * and centralises the dual-invalidation rule: every mutation invalidates BOTH
 * the list query AND the status query, so the "missing required" badge clears
 * the moment a required var is filled. The backend kicks off a real-time
 * service restart when a missing required var lands — the UI doesn't need to do
 * anything special beyond refetching status.</p>
 */
export function useBranchEnvVars(projectId: string, branchId: string, enabled: boolean) {
  const queryClient = useQueryClient()

  const listQuery = useGetApiProjectsProjectIdBranchesBranchIdEnv(projectId, branchId, {
    query: { enabled: enabled && !!projectId && !!branchId },
  })

  const statusQuery = useGetApiProjectsProjectIdBranchesBranchIdEnvStatus(
    projectId,
    branchId,
    { query: { enabled: enabled && !!projectId && !!branchId } },
  )

  const addMutation = usePostApiProjectsProjectIdBranchesBranchIdEnv()
  const updateMutation = usePutApiProjectsProjectIdBranchesBranchIdEnvKey()
  const deleteMutation = useDeleteApiProjectsProjectIdBranchesBranchIdEnvKey()

  /** Invalidate both the list and the status queries after any mutation. */
  const invalidateBoth = useCallback(() => {
    queryClient.invalidateQueries({
      queryKey: getGetApiProjectsProjectIdBranchesBranchIdEnvQueryKey(projectId, branchId),
    })
    queryClient.invalidateQueries({
      queryKey: getGetApiProjectsProjectIdBranchesBranchIdEnvStatusQueryKey(
        projectId,
        branchId,
      ),
    })
  }, [queryClient, projectId, branchId])

  const addVar = useCallback(
    async (key: string, value: string, isSecret: boolean) => {
      await addMutation.mutateAsync({
        projectId,
        branchId,
        data: { key, value, isSecret },
      })
      invalidateBoth()
    },
    [addMutation, projectId, branchId, invalidateBoth],
  )

  const updateVar = useCallback(
    async (key: string, value: string, isSecret: boolean) => {
      await updateMutation.mutateAsync({
        projectId,
        branchId,
        key,
        data: { value, isSecret },
      })
      invalidateBoth()
    },
    [updateMutation, projectId, branchId, invalidateBoth],
  )

  const deleteVar = useCallback(
    async (key: string) => {
      await deleteMutation.mutateAsync({ projectId, branchId, key })
      invalidateBoth()
    },
    [deleteMutation, projectId, branchId, invalidateBoth],
  )

  return {
    items: listQuery.data ?? [],
    status: statusQuery.data,
    isLoading: listQuery.isLoading || statusQuery.isLoading,
    isError: listQuery.isError || statusQuery.isError,
    refetch: () => {
      listQuery.refetch()
      statusQuery.refetch()
    },
    addVar,
    updateVar,
    deleteVar,
    isAdding: addMutation.isPending,
    isUpdating: updateMutation.isPending,
    isDeleting: deleteMutation.isPending,
  }
}

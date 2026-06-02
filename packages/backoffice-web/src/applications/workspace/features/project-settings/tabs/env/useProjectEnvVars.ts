import { useCallback, useState } from 'react'
import { useQueryClient } from '@tanstack/react-query'
import {
  getGetApiProjectsProjectIdBranchesBranchIdEnvQueryKey,
  getGetApiProjectsProjectIdBranchesBranchIdEnvStatusQueryKey,
  getGetApiProjectsProjectIdSecretsQueryKey,
  useDeleteApiProjectsProjectIdSecretsKey,
  useGetApiProjectsProjectIdSecrets,
  usePostApiProjectsProjectIdSecrets,
  usePutApiProjectsProjectIdSecretsKey,
} from '../../../../../../api/queries-commands'
import { readEnvVarApiErrorCode } from './envVarApiError'
import type { EnvVarListItem } from './envVarTypes'

/**
 * Project-wide default env vars (`BranchId == null`). Mutations invalidate the
 * project list and, when {@link branchId} is supplied, the branch-effective
 * list + status so missing badges clear without a reload.
 */
export function useProjectEnvVars(
  projectId: string,
  enabled: boolean,
  branchId?: string,
) {
  const queryClient = useQueryClient()

  const listQuery = useGetApiProjectsProjectIdSecrets(projectId, {
    query: { enabled: enabled && !!projectId },
  })

  const addMutation = usePostApiProjectsProjectIdSecrets()
  const updateMutation = usePutApiProjectsProjectIdSecretsKey()
  const deleteMutation = useDeleteApiProjectsProjectIdSecretsKey()
  const [isImporting, setIsImporting] = useState(false)

  const invalidateAll = useCallback(() => {
    queryClient.invalidateQueries({
      queryKey: getGetApiProjectsProjectIdSecretsQueryKey(projectId),
    })
    if (branchId) {
      queryClient.invalidateQueries({
        queryKey: getGetApiProjectsProjectIdBranchesBranchIdEnvQueryKey(projectId, branchId),
      })
      queryClient.invalidateQueries({
        queryKey: getGetApiProjectsProjectIdBranchesBranchIdEnvStatusQueryKey(
          projectId,
          branchId,
        ),
      })
    }
  }, [queryClient, projectId, branchId])

  const items: EnvVarListItem[] = (listQuery.data ?? []).map((row) => ({
    key: row.key,
    isSecret: true,
    version: row.version,
    updatedAt: row.updatedAt,
    scope: 'project',
    value: null,
  }))

  const addVar = useCallback(
    async (key: string, value: string) => {
      await addMutation.mutateAsync({ projectId, data: { key, value } })
      invalidateAll()
    },
    [addMutation, projectId, invalidateAll],
  )

  const updateVar = useCallback(
    async (key: string, value: string) => {
      await updateMutation.mutateAsync({ projectId, key, data: { value } })
      invalidateAll()
    },
    [updateMutation, projectId, invalidateAll],
  )

  const deleteVar = useCallback(
    async (key: string) => {
      await deleteMutation.mutateAsync({ projectId, key })
      invalidateAll()
    },
    [deleteMutation, projectId, invalidateAll],
  )

  const importVars = useCallback(
    async (entries: ReadonlyArray<{ key: string; value: string }>) => {
      setIsImporting(true)
      try {
        const existingKeys = new Set((listQuery.data ?? []).map((row) => row.key))
        for (const entry of entries) {
          if (existingKeys.has(entry.key)) {
            await updateMutation.mutateAsync({
              projectId,
              key: entry.key,
              data: { value: entry.value },
            })
            continue
          }

          try {
            await addMutation.mutateAsync({
              projectId,
              data: { key: entry.key, value: entry.value },
            })
            existingKeys.add(entry.key)
          } catch (err) {
            if (readEnvVarApiErrorCode(err) !== 'key_already_exists') {
              throw err
            }
            await updateMutation.mutateAsync({
              projectId,
              key: entry.key,
              data: { value: entry.value },
            })
            existingKeys.add(entry.key)
          }
        }
        invalidateAll()
      } finally {
        setIsImporting(false)
      }
    },
    [addMutation, updateMutation, projectId, listQuery.data, invalidateAll],
  )

  return {
    items,
    isLoading: listQuery.isLoading,
    isError: listQuery.isError,
    refetch: listQuery.refetch,
    addVar,
    updateVar,
    deleteVar,
    importVars,
    isAdding: addMutation.isPending,
    isUpdating: updateMutation.isPending,
    isDeleting: deleteMutation.isPending,
    isImporting,
  }
}

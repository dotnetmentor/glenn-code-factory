import { useCallback, useState } from 'react'
import { useQueryClient } from '@tanstack/react-query'
import {
  getGetApiProjectsProjectIdBranchesBranchIdEnvQueryKey,
  getGetApiProjectsProjectIdBranchesBranchIdEnvStatusQueryKey,
  getGetApiProjectsProjectIdSecretsQueryKey,
  useDeleteApiProjectsProjectIdBranchesBranchIdEnvKey,
  useGetApiProjectsProjectIdBranchesBranchIdEnv,
  useGetApiProjectsProjectIdBranchesBranchIdEnvStatus,
  usePostApiProjectsProjectIdBranchesBranchIdEnv,
  usePutApiProjectsProjectIdBranchesBranchIdEnvKey,
} from '../../../../../../api/queries-commands'
import { readEnvVarApiErrorCode } from './envVarApiError'
import type { EnvVarListItem } from './envVarTypes'

/**
 * Branch override env vars and branch-effective status. Writes always create or
 * rotate branch-specific rows; project defaults live in {@link useProjectEnvVars}.
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
  const [isImporting, setIsImporting] = useState(false)

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
    queryClient.invalidateQueries({
      queryKey: getGetApiProjectsProjectIdSecretsQueryKey(projectId),
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

  const importVars = useCallback(
    async (
      entries: ReadonlyArray<{ key: string; value: string; isSecret: boolean }>,
      branchOverrideKeys: ReadonlySet<string>,
    ) => {
      setIsImporting(true)
      try {
        const branchKeys = new Set(branchOverrideKeys)
        for (const entry of entries) {
          if (branchKeys.has(entry.key)) {
            await updateMutation.mutateAsync({
              projectId,
              branchId,
              key: entry.key,
              data: { value: entry.value, isSecret: entry.isSecret },
            })
            continue
          }

          try {
            await addMutation.mutateAsync({
              projectId,
              branchId,
              data: { key: entry.key, value: entry.value, isSecret: entry.isSecret },
            })
            branchKeys.add(entry.key)
          } catch (err) {
            if (readEnvVarApiErrorCode(err) !== 'key_already_exists') {
              throw err
            }
            await updateMutation.mutateAsync({
              projectId,
              branchId,
              key: entry.key,
              data: { value: entry.value, isSecret: entry.isSecret },
            })
            branchKeys.add(entry.key)
          }
        }
        invalidateBoth()
      } finally {
        setIsImporting(false)
      }
    },
    [addMutation, updateMutation, projectId, branchId, invalidateBoth],
  )

  return {
    items: listQuery.data ?? [],
    overrideItems: (listQuery.data ?? [])
      .filter((item) => item.scope === 'branch')
      .map(
        (item): EnvVarListItem => ({
          key: item.key,
          isSecret: item.isSecret,
          version: item.version,
          updatedAt: item.updatedAt,
          scope: 'branch',
          value: item.value,
        }),
      ),
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
    importVars,
    isAdding: addMutation.isPending,
    isUpdating: updateMutation.isPending,
    isDeleting: deleteMutation.isPending,
    isImporting,
  }
}

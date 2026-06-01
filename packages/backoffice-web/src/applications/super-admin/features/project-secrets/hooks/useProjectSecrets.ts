import { useQueryClient } from '@tanstack/react-query'
import { AxiosError } from 'axios'
import {
  useGetApiProjectsProjectIdSecrets,
  usePostApiProjectsProjectIdSecrets,
  usePutApiProjectsProjectIdSecretsKey,
  useDeleteApiProjectsProjectIdSecretsKey,
  getGetApiProjectsProjectIdSecretsQueryKey,
  ProblemDetails,
} from '@/api/queries-commands'
import { getErrorMessage } from '@/applications/shared/utils/errorUtils'

interface UseProjectSecretsProps {
  projectId: string
  onSuccess?: (message: string) => void
  onError?: (error: string) => void
}

/**
 * Wrapper around the generated Orval hooks for project secrets.
 * Centralises invalidation + error mapping for the env-vars UI.
 */
export function useProjectSecrets({ projectId, onSuccess, onError }: UseProjectSecretsProps) {
  const queryClient = useQueryClient()

  const listQuery = useGetApiProjectsProjectIdSecrets(projectId, {
    query: { enabled: !!projectId },
  })

  const invalidateList = () =>
    queryClient.invalidateQueries({
      queryKey: getGetApiProjectsProjectIdSecretsQueryKey(projectId),
    })

  const addSecret = usePostApiProjectsProjectIdSecrets({
    mutation: {
      onSuccess: () => {
        invalidateList()
        onSuccess?.('Variable added')
      },
      onError: (error: AxiosError<ProblemDetails>) => {
        onError?.(getErrorMessage(error))
      },
    },
  })

  const updateSecret = usePutApiProjectsProjectIdSecretsKey({
    mutation: {
      onSuccess: () => {
        invalidateList()
        onSuccess?.('Variable updated')
      },
      onError: (error: AxiosError<ProblemDetails>) => {
        onError?.(getErrorMessage(error))
      },
    },
  })

  const deleteSecret = useDeleteApiProjectsProjectIdSecretsKey({
    mutation: {
      onSuccess: () => {
        invalidateList()
        onSuccess?.('Variable deleted')
      },
      onError: (error: AxiosError<ProblemDetails>) => {
        onError?.(getErrorMessage(error))
      },
    },
  })

  return {
    secrets: listQuery.data ?? [],
    isLoading: listQuery.isLoading,
    isFetching: listQuery.isFetching,
    error: listQuery.error,

    addSecret: (key: string, value: string) =>
      addSecret.mutateAsync({ projectId, data: { key, value } }),
    updateSecret: (key: string, value: string) =>
      updateSecret.mutateAsync({ projectId, key, data: { value } }),
    deleteSecret: (key: string) =>
      deleteSecret.mutateAsync({ projectId, key }),

    isAdding: addSecret.isPending,
    isUpdating: updateSecret.isPending,
    isDeleting: deleteSecret.isPending,
  }
}

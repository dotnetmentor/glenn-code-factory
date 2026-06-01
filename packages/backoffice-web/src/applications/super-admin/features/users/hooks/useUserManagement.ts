import { useQueryClient } from '@tanstack/react-query'
import { AxiosError } from 'axios'
import {
  useGetApiUsersId,
  usePutApiUsersId,
  usePutApiUsersIdRoles,
  getGetApiUsersQueryKey,
  getGetApiUsersIdQueryKey,
  getGetApiUsersIdRolesQueryKey,
  ProblemDetails,
} from '../../../../../api/queries-commands'
import { getErrorMessage } from '@/applications/shared/utils/errorUtils'

interface UseUserManagementProps {
  userId: string
  onSuccess?: (message: string) => void
  onError?: (error: string) => void
}

export function useUserManagement({ userId, onSuccess, onError }: UseUserManagementProps) {
  const queryClient = useQueryClient()

  const userDetails = useGetApiUsersId(userId, {
    query: { enabled: !!userId }
  })

  const updateUserInfo = usePutApiUsersId({
    mutation: {
      onSuccess: () => {
        queryClient.invalidateQueries({ queryKey: getGetApiUsersQueryKey() })
        queryClient.invalidateQueries({ queryKey: getGetApiUsersIdQueryKey(userId) })
        onSuccess?.('User info updated')
      },
      onError: (error: AxiosError<ProblemDetails>) => {
        onError?.(getErrorMessage(error))
      }
    }
  })

  const updateUserRoles = usePutApiUsersIdRoles({
    mutation: {
      onSuccess: () => {
        queryClient.invalidateQueries({ queryKey: getGetApiUsersQueryKey() })
        queryClient.invalidateQueries({ queryKey: getGetApiUsersIdQueryKey(userId) })
        queryClient.invalidateQueries({ queryKey: getGetApiUsersIdRolesQueryKey(userId) })
        onSuccess?.('Roles updated')
      },
      onError: (error: AxiosError<ProblemDetails>) => {
        onError?.(getErrorMessage(error))
      }
    }
  })
  
  return {
    user: userDetails.data,
    isLoadingUser: userDetails.isLoading,
    isUpdatingInfo: updateUserInfo.isPending,
    isUpdatingRoles: updateUserRoles.isPending,
    
    updateInfo: (data: { firstName?: string; lastName?: string; email?: string }) =>
      updateUserInfo.mutate({ id: userId, data }),
    
    updateRoles: (roles: string[]) =>
      updateUserRoles.mutate({ id: userId, data: { roles } }),
    
    refetchUser: userDetails.refetch,
  }
}

import { useEffect, useRef, useState } from 'react'
import { useQueryClient } from '@tanstack/react-query'
import { AxiosError } from 'axios'
import {
  usePostApiProjectsProjectIdByok,
  getGetApiProjectsProjectIdQueryKey,
  ProblemDetails,
  UpdateProjectByokResponse,
} from '@/api/queries-commands'
import { getErrorMessage } from '@/applications/shared/utils/errorUtils'

interface UseProjectByokProps {
  projectId: string
  onSuccess?: (message: string) => void
  onError?: (message: string) => void
}

type ByokStatus = {
  hasCursorApiKey: boolean
} | null

/**
 * Wrapper around the generated Orval mutation for project BYOK credentials.
 *
 * Cursor-only: the backend never exposes the actual token (write-only). The
 * only way to learn whether a credential is configured is the `hasCursorApiKey`
 * flag in the `UpdateProjectByokResponse`.
 */
export function useProjectByok({ projectId, onSuccess, onError }: UseProjectByokProps) {
  const queryClient = useQueryClient()
  const [status, setStatus] = useState<ByokStatus>(null)
  const [isOwner, setIsOwner] = useState<boolean | null>(null)
  const [isLoadingStatus, setIsLoadingStatus] = useState(true)
  const suppressErrorToastRef = useRef(false)

  const invalidateProject = () =>
    queryClient.invalidateQueries({
      queryKey: getGetApiProjectsProjectIdQueryKey(projectId),
    })

  const updateMutation = usePostApiProjectsProjectIdByok({
    mutation: {
      onError: (error: AxiosError<ProblemDetails>) => {
        if (suppressErrorToastRef.current) return
        onError?.(getErrorMessage(error))
      },
    },
  })

  useEffect(() => {
    if (!projectId) return
    let cancelled = false
    setIsLoadingStatus(true)
    suppressErrorToastRef.current = true

    updateMutation
      .mutateAsync({
        projectId,
        data: {
          setCursorApiKey: false,
        },
      })
      .then((response: UpdateProjectByokResponse) => {
        if (cancelled) return
        setStatus({
          hasCursorApiKey: response.hasCursorApiKey,
        })
        setIsOwner(true)
        setIsLoadingStatus(false)
      })
      .catch((err: unknown) => {
        if (cancelled) return
        const axiosErr = err as AxiosError<ProblemDetails>
        if (axiosErr?.response?.status === 404) {
          setIsOwner(false)
        } else {
          setIsOwner(null)
        }
        setIsLoadingStatus(false)
      })
      .finally(() => {
        if (cancelled) return
        suppressErrorToastRef.current = false
      })

    return () => {
      cancelled = true
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [projectId])

  const applyResponse = (response: UpdateProjectByokResponse) => {
    setStatus({
      hasCursorApiKey: response.hasCursorApiKey,
    })
    invalidateProject()
  }

  const saveCursorApiKey = async (value: string) => {
    const response = await updateMutation.mutateAsync({
      projectId,
      data: {
        setCursorApiKey: true,
        cursorApiKey: value,
      },
    })
    applyResponse(response)
    onSuccess?.('Saved')
    return response
  }

  const clearCursorApiKey = async () => {
    const response = await updateMutation.mutateAsync({
      projectId,
      data: {
        setCursorApiKey: true,
        cursorApiKey: null,
      },
    })
    applyResponse(response)
    onSuccess?.('Cleared')
    return response
  }

  return {
    status,
    isOwner,
    isLoadingStatus,
    isUpdating: updateMutation.isPending,
    saveCursorApiKey,
    clearCursorApiKey,
  }
}

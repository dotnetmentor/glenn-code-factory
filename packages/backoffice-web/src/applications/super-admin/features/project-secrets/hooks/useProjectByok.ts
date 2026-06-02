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

export type ProjectByokStatus = {
  hasCursorApiKey: boolean
  hasWorkspaceCursorApiKey: boolean
  allowProjectCursorApiKeyOverride: boolean
  hasEffectiveCursorApiKey: boolean
} | null

/**
 * Wrapper around the generated Orval mutation for project BYOK credentials.
 */
export function useProjectByok({ projectId, onSuccess, onError }: UseProjectByokProps) {
  const queryClient = useQueryClient()
  const [status, setStatus] = useState<ProjectByokStatus>(null)
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
          hasWorkspaceCursorApiKey: response.hasWorkspaceCursorApiKey,
          allowProjectCursorApiKeyOverride: response.allowProjectCursorApiKeyOverride,
          hasEffectiveCursorApiKey: response.hasEffectiveCursorApiKey,
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
      hasWorkspaceCursorApiKey: response.hasWorkspaceCursorApiKey,
      allowProjectCursorApiKeyOverride: response.allowProjectCursorApiKeyOverride,
      hasEffectiveCursorApiKey: response.hasEffectiveCursorApiKey,
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

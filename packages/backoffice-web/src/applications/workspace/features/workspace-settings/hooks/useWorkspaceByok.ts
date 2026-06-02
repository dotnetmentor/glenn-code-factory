import { useEffect, useRef, useState } from 'react'
import { AxiosError } from 'axios'
import {
  postApiWorkspacesSlugByok,
  ProblemDetails,
  UpdateWorkspaceByokRequest,
  UpdateWorkspaceByokResponse,
} from '@/api/queries-commands'
import { getErrorMessage } from '@/applications/shared/utils/errorUtils'

interface UseWorkspaceByokProps {
  workspaceSlug: string | null
  onSuccess?: (message: string) => void
  onError?: (message: string) => void
}

type WorkspaceByokStatus = {
  hasCursorApiKey: boolean
  allowProjectCursorApiKeyOverride: boolean
} | null

export function useWorkspaceByok({
  workspaceSlug,
  onSuccess,
  onError,
}: UseWorkspaceByokProps) {
  const [status, setStatus] = useState<WorkspaceByokStatus>(null)
  const [canManage, setCanManage] = useState<boolean | null>(null)
  const [isLoadingStatus, setIsLoadingStatus] = useState(true)
  const [isUpdating, setIsUpdating] = useState(false)
  const suppressErrorToastRef = useRef(false)

  useEffect(() => {
    if (!workspaceSlug) return
    let cancelled = false
    setIsLoadingStatus(true)
    suppressErrorToastRef.current = true

    postApiWorkspacesSlugByok(workspaceSlug, {
      setCursorApiKey: false,
      setAllowProjectCursorApiKeyOverride: false,
    })
      .then((response: UpdateWorkspaceByokResponse) => {
        if (cancelled) return
        setStatus({
          hasCursorApiKey: response.hasCursorApiKey,
          allowProjectCursorApiKeyOverride: response.allowProjectCursorApiKeyOverride,
        })
        setCanManage(true)
        setIsLoadingStatus(false)
      })
      .catch((err: unknown) => {
        if (cancelled) return
        const axiosErr = err as AxiosError<ProblemDetails>
        if (axiosErr?.response?.status === 403 || axiosErr?.response?.status === 401) {
          setCanManage(false)
        } else {
          setCanManage(null)
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
  }, [workspaceSlug])

  const applyResponse = (response: UpdateWorkspaceByokResponse) => {
    setStatus({
      hasCursorApiKey: response.hasCursorApiKey,
      allowProjectCursorApiKeyOverride: response.allowProjectCursorApiKeyOverride,
    })
  }

  const runMutation = async (data: Parameters<typeof postApiWorkspacesSlugByok>[1]) => {
    if (!workspaceSlug) return
    setIsUpdating(true)
    try {
      const response = await postApiWorkspacesSlugByok(workspaceSlug, data)
      applyResponse(response)
      onSuccess?.('Saved')
      return response
    } catch (err) {
      if (!suppressErrorToastRef.current) {
        onError?.(getErrorMessage(err as AxiosError<ProblemDetails>))
      }
      throw err
    } finally {
      setIsUpdating(false)
    }
  }

  const saveCursorApiKey = (value: string) =>
    runMutation({
      setCursorApiKey: true,
      cursorApiKey: value,
      setAllowProjectCursorApiKeyOverride: false,
    } satisfies UpdateWorkspaceByokRequest)

  const clearCursorApiKey = () =>
    runMutation({
      setCursorApiKey: true,
      cursorApiKey: null,
      setAllowProjectCursorApiKeyOverride: false,
    } satisfies UpdateWorkspaceByokRequest)

  const setAllowProjectOverride = (allow: boolean) =>
    runMutation({
      setCursorApiKey: false,
      setAllowProjectCursorApiKeyOverride: true,
      allowProjectCursorApiKeyOverride: allow,
    } satisfies UpdateWorkspaceByokRequest)

  return {
    status,
    canManage,
    isLoadingStatus,
    isUpdating,
    saveCursorApiKey,
    clearCursorApiKey,
    setAllowProjectOverride,
  }
}

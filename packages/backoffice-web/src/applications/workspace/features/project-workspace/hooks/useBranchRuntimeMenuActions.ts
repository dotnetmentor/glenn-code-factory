import { useQueryClient } from '@tanstack/react-query'
import {
  getGetApiProjectsProjectIdBranchesBranchIdRuntimeStatusQueryKey,
  getGetApiProjectsProjectIdBranchesQueryKey,
  getGetApiWorkspacesSlugProjectsQueryKey,
  usePostApiProjectsProjectIdBranchesBranchIdRuntimeForceStop,
  usePostApiProjectsProjectIdBranchesBranchIdRuntimeResetFromScratch,
  usePostApiProjectsProjectIdBranchesBranchIdRuntimeRestart,
  usePostApiProjectsProjectIdBranchesBranchIdRuntimeSuspend,
} from '../../../../../api/queries-commands'
import { useNotification } from '../../../../shared/contexts/NotificationContext'

export interface UseBranchRuntimeMenuActionsParams {
  projectId: string
  branchId: string
  branchName: string
  slug: string
  isSuspended: boolean
  isFailed: boolean
  onCloseMenu: () => void
}

export function useBranchRuntimeMenuActions({
  projectId,
  branchId,
  branchName,
  slug,
  isSuspended,
  isFailed,
  onCloseMenu,
}: UseBranchRuntimeMenuActionsParams) {
  const queryClient = useQueryClient()
  const { showSuccess, showError } = useNotification()

  const restartMut = usePostApiProjectsProjectIdBranchesBranchIdRuntimeRestart()
  const suspendMut = usePostApiProjectsProjectIdBranchesBranchIdRuntimeSuspend()
  const forceStopMut =
    usePostApiProjectsProjectIdBranchesBranchIdRuntimeForceStop()
  const resetFromScratchMut =
    usePostApiProjectsProjectIdBranchesBranchIdRuntimeResetFromScratch()

  const invalidateRuntimeStatus = () => {
    void queryClient.invalidateQueries({
      queryKey: getGetApiProjectsProjectIdBranchesBranchIdRuntimeStatusQueryKey(
        projectId,
        branchId,
      ),
    })
  }

  const invalidateBranchLists = () => {
    queryClient.invalidateQueries({
      queryKey: getGetApiProjectsProjectIdBranchesQueryKey(projectId),
    })
    queryClient.invalidateQueries({
      queryKey: getGetApiWorkspacesSlugProjectsQueryKey(slug),
    })
  }

  const handleRestartRuntime = () => {
    onCloseMenu()
    restartMut.mutate(
      { projectId, branchId },
      {
        onSuccess: () => {
          showSuccess(
            isSuspended
              ? `Waking runtime for "${branchName}".`
              : isFailed
                ? `Restarting runtime for "${branchName}".`
                : `Restart requested for "${branchName}".`,
          )
          invalidateRuntimeStatus()
        },
        onError: (err: unknown) => {
          showError(mapBranchRuntimeActionError(err))
        },
      },
    )
  }

  const handlePutToSleep = () => {
    onCloseMenu()
    suspendMut.mutate(
      { projectId, branchId },
      {
        onSuccess: () => {
          showSuccess(`Putting "${branchName}" to sleep.`)
          invalidateRuntimeStatus()
          invalidateBranchLists()
        },
        onError: (err: unknown) => {
          showError(mapBranchRuntimeActionError(err))
        },
      },
    )
  }

  const handleForceStop = () => {
    onCloseMenu()
    forceStopMut.mutate(
      { projectId, branchId },
      {
        onSuccess: () => {
          showSuccess(`Force-stopping runtime for "${branchName}".`)
          invalidateRuntimeStatus()
          invalidateBranchLists()
        },
        onError: (err: unknown) => {
          showError(mapBranchRuntimeActionError(err))
        },
      },
    )
  }

  const handleResetFromScratch = () => {
    onCloseMenu()
    const confirmed = window.confirm(
      `Start "${branchName}" from scratch?\n\nThis wipes the remote workspace disk and reprovisions a fresh runtime. Uncommitted work on the machine will be lost.`,
    )
    if (!confirmed) {
      return
    }
    resetFromScratchMut.mutate(
      { projectId, branchId },
      {
        onSuccess: () => {
          showSuccess(
            `Wiping disk and reprovisioning "${branchName}" from scratch.`,
          )
          invalidateRuntimeStatus()
          invalidateBranchLists()
        },
        onError: (err: unknown) => {
          showError(mapBranchRuntimeActionError(err))
        },
      },
    )
  }

  return {
    handleRestartRuntime,
    handlePutToSleep,
    handleForceStop,
    handleResetFromScratch,
    isRestartPending: restartMut.isPending,
    isSuspendPending: suspendMut.isPending,
    isForceStopPending: forceStopMut.isPending,
    isResetFromScratchPending: resetFromScratchMut.isPending,
  }
}

function mapBranchRuntimeActionError(err: unknown): string {
  const data = (err as {
    response?: { data?: { error?: string; detail?: string }; status?: number }
  })?.response?.data
  const raw = data?.error ?? data?.detail
  if (raw) {
    return raw.replace(/^(conflict:|not-found:)\s*/, '').trim()
  }
  if ((err as { response?: { status?: number } })?.response?.status === 409) {
    return "Runtime is in a state that can't be restarted right now."
  }
  return "Couldn't restart the runtime. Try again in a moment."
}

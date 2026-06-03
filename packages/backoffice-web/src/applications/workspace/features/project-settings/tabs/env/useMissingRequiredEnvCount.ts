import { useGetApiProjectsProjectIdBranchesBranchIdEnvStatus } from '../../../../../../api/queries-commands'
import { missingRequiredEnvItems } from './requiredEnvStatus'

/**
 * Count of required env vars not yet satisfied on the given branch. Drives the
 * "missing variables" badge on the Environment item in the project-settings
 * drawer nav, so the indicator is visible without opening the tab.
 *
 * <p>Branch-scoped — required-var status is only meaningful per branch. Returns
 * 0 when there's no branch context (the drawer can be opened without one) or
 * while the status query is still loading.</p>
 */
export function useMissingRequiredEnvCount(
  projectId: string,
  branchId: string | undefined,
  enabled: boolean,
): number {
  const statusQuery = useGetApiProjectsProjectIdBranchesBranchIdEnvStatus(
    projectId,
    branchId ?? '',
    { query: { enabled: enabled && !!projectId && !!branchId } },
  )
  return missingRequiredEnvItems(statusQuery.data).length
}

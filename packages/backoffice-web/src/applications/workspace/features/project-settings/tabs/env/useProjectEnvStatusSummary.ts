import { useMemo } from 'react'
import { useGetApiProjectsProjectIdSecretsStatusSummary } from '../../../../../../api/queries-commands'

export interface ProjectEnvStatusSummary {
  isLoading: boolean
  /** True when at least one non-archived branch has a missing required var. */
  hasAnyMissing: boolean
  /** Number of branches with at least one missing required var. */
  branchesWithMissing: number
  /** Missing required-var count for a specific branch (0 when none/unknown). */
  missingForBranch: (branchId: string | null | undefined) => number
}

/**
 * Project-wide rollup of branches with missing required env vars. One request
 * (via {@code GET /api/projects/{id}/secrets/status-summary}) powers the
 * cross-branch indicators: per-branch dots in the sidebar and the
 * settings-cog badge — without fanning out a per-branch status call.
 */
export function useProjectEnvStatusSummary(
  projectId: string,
  enabled = true,
): ProjectEnvStatusSummary {
  const query = useGetApiProjectsProjectIdSecretsStatusSummary(projectId, {
    query: { enabled: enabled && !!projectId },
  })

  const byBranch = useMemo(() => {
    const map = new Map<string, number>()
    for (const b of query.data?.branches ?? []) {
      map.set(b.branchId, b.missingCount)
    }
    return map
  }, [query.data])

  return {
    isLoading: query.isLoading,
    hasAnyMissing: (query.data?.branchesWithMissing ?? 0) > 0,
    branchesWithMissing: query.data?.branchesWithMissing ?? 0,
    missingForBranch: (branchId) =>
      branchId ? (byBranch.get(branchId) ?? 0) : 0,
  }
}

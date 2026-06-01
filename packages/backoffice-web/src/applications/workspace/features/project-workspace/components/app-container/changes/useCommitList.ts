/**
 * Commit-list fetch for the CompareAgainstPicker.
 *
 * <p>Thin wrapper around the generated Orval hook
 * {@code useGetApiRuntimesRuntimeIdDiffCommits}. Maps the backend's
 * {@code CommitInfo} shape to the picker's {@code CommitListItem} shape
 * (renamed fields for readability) and folds React-Query state into
 * the simple {@code UseCommitListResult} contract the picker expects.</p>
 */

import { useGetApiRuntimesRuntimeIdDiffCommits } from '../../../../../../../api/queries-commands'

/**
 * One commit row in the picker dropdown.
 *
 * <p>Matches the {@code base..HEAD} range returned by the backend's
 * {@code GET /api/runtimes/{runtimeId}/diff/commits} endpoint.</p>
 */
export interface CommitListItem {
  /** Full 40-char SHA — used as the {@code commit.sha} scope value. */
  sha: string
  /** First line of the commit message, trimmed by the backend. */
  subject: string
  /** ISO-8601 author time. */
  authoredAt: string
  /** Commit author display name. */
  author: string
}

interface UseCommitListOptions {
  runtimeId: string
  /** Base ref/SHA. The list spans {@code base..HEAD}. */
  base: string
  /** Only fetch when the picker has actually opened the commit menu. */
  enabled: boolean
  /** Hard cap — server is expected to respect this. */
  limit?: number
}

interface UseCommitListResult {
  commits: CommitListItem[]
  total: number
  /** True while the first fetch is in flight. */
  isLoading: boolean
  /** True if the base ref couldn't be resolved on the runtime. */
  isError: boolean
  /** True iff {@code commits.length < total}. */
  hasMore: boolean
  /** Re-runs the fetch from scratch. */
  refetch: () => void
}

/**
 * Returns the commit list spanning {@code base..HEAD} on the runtime.
 */
export function useCommitList({
  runtimeId,
  base,
  enabled,
  limit = 200,
}: UseCommitListOptions): UseCommitListResult {
  const query = useGetApiRuntimesRuntimeIdDiffCommits(
    runtimeId,
    { base, head: 'HEAD', limit },
    {
      query: {
        enabled: enabled && !!runtimeId && !!base,
        // Commit history changes whenever the agent commits — but within
        // a picker session the list is effectively immutable. 30s is a
        // pragmatic cache so re-opening the menu feels instant.
        staleTime: 30_000,
      },
    },
  )

  const commits: CommitListItem[] = (query.data?.commits ?? []).map((c) => ({
    sha: c.sha,
    subject: c.message,
    authoredAt: c.authorDate,
    author: c.authorName,
  }))

  return {
    commits,
    total: commits.length,
    isLoading: query.isLoading,
    isError: query.isError,
    // Backend already truncates at {@code limit}; treat a full page as
    // "possibly more" so the picker can offer a higher cap if needed.
    hasMore: commits.length >= limit,
    refetch: () => {
      void query.refetch()
    },
  }
}

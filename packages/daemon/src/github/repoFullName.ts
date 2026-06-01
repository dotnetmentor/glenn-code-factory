// repoFullName — extract the `owner/repo` identifier from a GitHub HTTPS clone
// URL.
//
// The GitHub App installation-token endpoint scopes a token to a single
// repository identified by `owner/name`. The daemon receives a clone URL from
// the bootstrap payload (`RepoConfig.url`) and must derive that identifier
// before invoking `IRuntimeHub.GetRepoAccessToken`. Centralising the parse here
// keeps the stage code free of regex and gives us a single throw-loud failure
// path: an unexpected URL shape (legacy SSH, git@, file://, …) is a programmer
// error, not a transient runtime condition.

/**
 * Extract `owner/repo` from an HTTPS GitHub clone URL.
 *
 * Accepts:
 *   - `https://github.com/owner/repo.git`
 *   - `https://github.com/owner/repo`
 *
 * Throws on any other shape — including SSH (`git@github.com:owner/repo.git`),
 * `http://` (we require TLS), bare paths, and any URL that doesn't have
 * exactly two path segments after the host.
 */
export function parseRepoFullName(cloneUrl: string): string {
  if (typeof cloneUrl !== 'string' || cloneUrl.length === 0) {
    throw new Error(`parseRepoFullName: expected non-empty string, got ${typeof cloneUrl}`)
  }

  // SSH / scp-style URLs (`git@github.com:owner/repo.git`) are rejected
  // explicitly so the operator gets a clear error instead of a confusing
  // "expected https://" further down. The daemon no longer supports the SSH
  // deploy-key path — installation tokens are HTTPS-only.
  if (cloneUrl.startsWith('git@') || cloneUrl.startsWith('ssh://')) {
    throw new Error(
      `parseRepoFullName: SSH-style URLs are no longer supported (${cloneUrl}); use https://github.com/owner/repo[.git]`,
    )
  }

  if (!cloneUrl.startsWith('https://github.com/')) {
    throw new Error(
      `parseRepoFullName: expected https://github.com/owner/repo[.git], got ${cloneUrl}`,
    )
  }

  // Strip the prefix and any trailing `.git` suffix, then verify exactly two
  // non-empty path segments remain.
  const tail = cloneUrl.slice('https://github.com/'.length).replace(/\.git$/i, '')
  const parts = tail.split('/')
  if (parts.length !== 2 || parts[0] === undefined || parts[1] === undefined) {
    throw new Error(
      `parseRepoFullName: expected exactly owner/repo path segments, got ${cloneUrl}`,
    )
  }
  const [owner, repo] = parts
  if (owner.length === 0 || repo.length === 0) {
    throw new Error(
      `parseRepoFullName: owner or repo segment is empty in ${cloneUrl}`,
    )
  }
  return `${owner}/${repo}`
}

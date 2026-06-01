/**
 * Parse a GitHub repository URL into its {@code owner} and {@code name}
 * components.
 *
 * <p>Used by the "Start from GitHub URL" entry point on the New Project page,
 * where the user pastes any public/accessible GitHub repo URL and we route
 * the create flow through the existing connect-existing-repo path with the
 * parsed coordinates + default branch.
 *
 * <p>Accepted shapes (all return {@code { owner, name }}):
 * <ul>
 *   <li>{@code https://github.com/owner/repo}</li>
 *   <li>{@code https://github.com/owner/repo/}</li>
 *   <li>{@code https://github.com/owner/repo.git}</li>
 *   <li>{@code https://github.com/owner/repo/tree/branch}</li>
 *   <li>{@code http://github.com/owner/repo}</li>
 *   <li>{@code github.com/owner/repo}</li>
 *   <li>{@code owner/repo}</li>
 * </ul>
 *
 * <p>Returns {@code null} for anything that doesn't match. We do not try to
 * be clever about query strings, ssh URLs, or non-github.com hosts — those
 * are out of scope for V1.
 */
export function parseGithubUrl(
  raw: string,
): { owner: string; name: string } | null {
  if (typeof raw !== 'string') return null
  const trimmed = raw.trim()
  if (trimmed.length === 0) return null

  // Strip a leading protocol so we can treat https/http/no-protocol uniformly.
  // Also tolerate a leading "git@github.com:owner/repo.git" but only if we
  // can normalise it into the same owner/name shape.
  let body = trimmed
    .replace(/^https?:\/\//i, '')
    .replace(/^git@github\.com:/i, 'github.com/')

  // GitHub-host gate. If the body looks like it has a hostname (i.e. a dot
  // in the first segment before the first slash) AND that hostname isn't
  // github.com, reject. This catches gitlab.com, bitbucket.org, host:port
  // shapes, and arbitrary domain spoofs while still letting us accept the
  // shorthand "owner/repo" form (no dotted host segment up front).
  const firstSlash = body.indexOf('/')
  const head = firstSlash < 0 ? body : body.slice(0, firstSlash)
  if (head.includes('.') && head.toLowerCase() !== 'github.com') {
    return null
  }

  // Drop a leading github.com/ if present. Without it we treat the input as
  // a bare "owner/repo".
  body = body.replace(/^github\.com\//i, '')

  // We only care about the first two segments: owner / repo. Anything after
  // (e.g. /tree/branch, /pull/123) is allowed but ignored.
  const segments = body.split('/').filter((s) => s.length > 0)
  if (segments.length < 2) return null

  const owner = segments[0]
  let name = segments[1]

  // Strip a trailing ".git" suffix, which is valid on clone URLs but not
  // part of the GitHub-API identity of the repo.
  if (name.toLowerCase().endsWith('.git')) {
    name = name.slice(0, -4)
  }

  // Loose validation: GitHub repo + owner names only allow a constrained
  // character set. We reject obvious garbage early but leave the final
  // verdict to the backend (which actually talks to GitHub).
  const ID_RE = /^[A-Za-z0-9._-]+$/
  if (!ID_RE.test(owner) || !ID_RE.test(name)) return null
  if (owner.length === 0 || name.length === 0) return null

  return { owner, name }
}

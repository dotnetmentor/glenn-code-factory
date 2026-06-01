// basicAuth — shared encoder for the value half of the
// `Authorization: Basic …` header GitHub's git HTTP backend expects when
// authenticating with a GitHub App installation token.
//
// === Why this lives here (not inlined in each caller) ===
//
// Both `CloningRepoStage` (initial clone / fetch+reset) and `GitModule`
// (push / fetch / reconcile during normal operation) need to mint the same
// header off the same token. The `x-access-token:<token>` username/password
// shape is documented by GitHub for app-installation auth — see
// docs.github.com/apps/creating-github-apps/authenticating-with-a-github-app/
// authenticating-as-a-github-app-installation. Centralising the encoder
// keeps the two sites in lock-step (a future change to the credential pair —
// e.g. moving to `Bearer` if GitHub ever flips the git backend over —
// happens once, here).
//
// === Why Basic, not Bearer ===
//
// GitHub's REST API accepts `Authorization: Bearer <installation-token>`,
// but GitHub's **git HTTP backend** does NOT — it only speaks HTTP Basic
// auth. With a Bearer header, GitHub silently rejects the request, git
// falls back to prompting for credentials, and on a non-TTY runtime that
// produces the famously cryptic
//   `fatal: could not read Username for 'https://github.com'`
// error. Stick with Basic.

/**
 * Encode an installation token into the value half of an
 * `Authorization: Basic …` header. GitHub's git HTTP backend expects
 * `x-access-token` as the username and the installation token as the
 * password — same shape it documents for HTTPS clones with a PAT.
 */
export function encodeBasicAuth(token: string): string {
  return Buffer.from(`x-access-token:${token}`, 'utf8').toString('base64')
}

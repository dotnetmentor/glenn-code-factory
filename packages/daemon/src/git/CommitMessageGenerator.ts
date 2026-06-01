// CommitMessageGenerator — Card 7 of daemon-git-ops.
//
// Tiny pure helper that turns a user-supplied prompt into a single-line commit
// subject. Lives next to GitModule because the auto-commit loop is the only
// caller today; broken out so future callers (e.g. the destructive-op gate)
// can reuse the same shape without depending on the orchestrator.
//
// Invariants worth pinning here:
//
//   - One line. Internal whitespace (including embedded newlines) collapses
//     to single spaces so the output is always a clean subject line.
//
//   - Bounded. Truncated to 60 visible chars including a trailing `...`
//     ellipsis so the wire payload + git's own subject-length conventions
//     stay sane.
//
//   - Empty fallback. If the user gave us nothing useful, we still produce a
//     valid Conventional-Commits-style subject so downstream tooling that
//     parses commit logs doesn't choke.

const MAX_SUBJECT_CHARS = 60
const ELLIPSIS = '...'
const PREFIX = 'chore(turn): '
const FALLBACK = `${PREFIX}no description`

export function buildCommitMessage(input: { userPrompt?: string }): string {
  const trimmed = (input.userPrompt ?? '').trim()
  if (trimmed.length === 0) return FALLBACK

  // Collapse runs of whitespace (including embedded newlines + tabs) so the
  // commit stays single-line. Apply BEFORE truncation so we don't waste budget
  // on whitespace runs that would have collapsed anyway.
  const sanitised = trimmed.replace(/\s+/g, ' ')

  const truncated =
    sanitised.length > MAX_SUBJECT_CHARS
      ? sanitised.slice(0, MAX_SUBJECT_CHARS - ELLIPSIS.length) + ELLIPSIS
      : sanitised

  return `${PREFIX}${truncated}`
}

/**
 * CommitTrailerLine — quiet "Committed and pushed to {branch} · {sha}" row
 * (P3.1) rendered as the LAST child under the final assistant bubble of an
 * auto-commit-producing turn. Sibling of {@link TurnStatusLine} — same
 * visual idiom (6px terminal dot, italic 13px muted text, no chip / border /
 * background, same {@code pl: 1.25} indent) so two consecutive turns read as
 * one continuous transcript rhythm.
 *
 * <p>Two visual variants:</p>
 * <ul>
 *   <li><b>Success</b> — accent-colored dot at 0.6 opacity, italic text, copy
 *       "Committed and pushed [to {branch}] · {shortSha}". The branch is
 *       suppressed when it equals the project's default branch (the chat
 *       conversation lives on the project's default branch in the common
 *       case, so naming it would be visual noise).</li>
 *   <li><b>Failure</b> — rust dot at 0.7 opacity, NON-italic text (italic
 *       reads frivolous on failure; same call as TurnStatusLine's failed
 *       state), copy "Committed locally — push failed ({reason}) ·
 *       {shortSha}". The commit DID land locally, so the link still points
 *       at the commit URL — users will see a 404 if they click before the
 *       eventual retry succeeds, which is acceptable.</li>
 * </ul>
 *
 * <p>The {@code shortSha} is a quiet underlined link to the GitHub commit URL
 * (or {@code null} if the project has no GitHub repo configured — the row
 * still renders, just with the sha as plain text). Link styling matches the
 * "Try again" link in {@link TurnStatusLine} — no MUI Link chrome, low-
 * contrast underline that brightens on hover/focus.</p>
 */
import { Box, Typography } from '@mui/material'

import { chromeTokens, semanticTokens, surfaceTokens } from '../../../shared/designTokens'
import { FAILURE_RUST_COLOR, RUNTIME_DOT_COLOR } from './chatEvents'

const tokens = {
  ...surfaceTokens,
  ...chromeTokens,
  runtimeFailed: semanticTokens.error,
  accent: RUNTIME_DOT_COLOR,
  failureRust: FAILURE_RUST_COLOR,
} as const

export interface CommitTrailerLineProps {
  commitSha: string
  shortSha: string
  branch: string
  pushed: boolean
  pushFailureReason: string | null
  /**
   * Project's GitHub owner / name — used to build the commit URL. Either
   * being empty / undefined collapses the sha-link affordance to plain text.
   */
  githubRepoOwner?: string | null
  githubRepoName?: string | null
  /**
   * Project's default branch name. When {@code branch} matches this we
   * suppress the "to {branch}" clause so the line reads just "Committed and
   * pushed · {shortSha}".
   */
  defaultBranchName?: string | null
}

export function CommitTrailerLine({
  commitSha,
  shortSha,
  branch,
  pushed,
  pushFailureReason,
  githubRepoOwner,
  githubRepoName,
  defaultBranchName,
}: CommitTrailerLineProps) {
  const hasRepoUrl =
    typeof githubRepoOwner === 'string' &&
    githubRepoOwner.length > 0 &&
    typeof githubRepoName === 'string' &&
    githubRepoName.length > 0
  const commitUrl = hasRepoUrl
    ? `https://github.com/${githubRepoOwner}/${githubRepoName}/commit/${commitSha}`
    : null

  // Suppress the branch clause on the success path when the commit landed on
  // the project's default branch — that's the common case (the conversation
  // lives on the default branch) and naming it adds no information.
  const shouldShowBranch =
    branch.length > 0 &&
    (defaultBranchName === undefined ||
      defaultBranchName === null ||
      branch !== defaultBranchName)

  // Build the prefix copy — everything before the " · {sha}" tail. The dot
  // separator + sha link are appended uniformly downstream so success and
  // failure rendering paths share the same trailing affordance.
  let prefix: string
  if (pushed) {
    prefix = shouldShowBranch
      ? `Committed and pushed to ${branch}`
      : 'Committed and pushed'
  } else {
    const reason = pushFailureReason ?? 'Unknown'
    prefix = `Committed locally — push failed (${reason})`
  }

  return (
    <Box
      data-commit-sha={commitSha}
      sx={{
        display: 'flex',
        alignItems: 'center',
        gap: 1,
        pl: 1.25,
        pr: 0.5,
        minHeight: 24,
      }}
    >
      {/* Terminal dot — static, never animated. The trailer always lands
          AFTER the work has settled (commit + push attempt complete), so a
          breathing pulse would lie about ongoing activity. */}
      <Box
        aria-hidden
        sx={{
          width: 6,
          height: 6,
          borderRadius: '50%',
          backgroundColor: pushed ? tokens.accent : tokens.failureRust,
          flexShrink: 0,
          opacity: pushed ? 0.6 : 0.7,
        }}
      />

      {/* Body — italic on success (matches TurnStatusLine's calm idiom),
          non-italic on failure (italic on failure reads frivolous). */}
      <Typography
        component="span"
        sx={{
          flex: 1,
          minWidth: 0,
          color: pushed ? tokens.textMuted : tokens.failureRust,
          fontStyle: pushed ? 'italic' : 'normal',
          fontSize: 13,
          letterSpacing: '0.01em',
          whiteSpace: 'nowrap',
          overflow: 'hidden',
          textOverflow: 'ellipsis',
        }}
      >
        {prefix}
        {' · '}
        {commitUrl ? (
          <Box
            component="a"
            href={commitUrl}
            target="_blank"
            rel="noopener noreferrer"
            sx={{
              color: 'inherit',
              fontStyle: 'inherit',
              textDecoration: 'underline',
              textUnderlineOffset: '2px',
              textDecorationColor: 'rgba(0, 0, 0, 0.18)',
              transition:
                'color 200ms ease, text-decoration-color 200ms ease',
              '&:hover': {
                color: tokens.textPrimary,
                textDecorationColor: 'rgba(0, 0, 0, 0.4)',
              },
              '&:focus-visible': {
                outline: 'none',
                color: tokens.textPrimary,
                textDecorationColor: 'rgba(0, 0, 0, 0.4)',
              },
            }}
          >
            {shortSha}
          </Box>
        ) : (
          <Box component="span" sx={{ fontFamily: 'monospace' }}>
            {shortSha}
          </Box>
        )}
      </Typography>
    </Box>
  )
}

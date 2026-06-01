import { useEffect, useMemo, useRef, useState } from 'react'
import {
  Box,
  CircularProgress,
  ClickAwayListener,
  Popper,
  Radio,
  Stack,
  TextField,
  Tooltip,
  Typography,
} from '@mui/material'
import KeyboardArrowDownIcon from '@mui/icons-material/KeyboardArrowDown'
import { appContainerTokens } from '../tokens'
import { workspaceAccent, workspaceFontFamily } from '@/applications/workspace/shared/designTokens'
import type { CompareScope } from './types'
import { useCommitList, type CommitListItem } from './useCommitList'

type PickerMode = 'workingTree' | 'branch' | 'commit'

interface CompareAgainstPickerProps {
  /** Project id — reserved for future commit-cache scoping. */
  projectId: string
  /** Runtime to query for the commit list. */
  runtimeId: string
  /** Current scope, owned by the parent. */
  scope: CompareScope
  /** Promote a new scope to the parent. */
  onScopeChange: (next: CompareScope) => void
  /**
   * Name of the branch the user is currently on. Used to detect the
   * "you're on the base branch" edge case so the picker can render that
   * disabled-with-tooltip treatment from the spec.
   */
  currentBranch?: string
  /** When true, the trigger and menu render in the desaturated style. */
  disabled?: boolean
}

/**
 * "Compare against" picker — the chrome affordance that swaps the Changes
 * tab's compare scope between working-tree, a branch base, and a
 * specific commit on the current branch.
 *
 * <p>Visual language matches PreviewChrome's URL pill: a quiet
 * paper-tone trigger that opens an MUI {@code Popper} menu. The menu
 * holds three rows (Working tree / Branch / Commit) each gated by a
 * radio. Picking a row commits the scope to the parent — there is no
 * "Apply" button; the picker is calm, not transactional.</p>
 */
export function CompareAgainstPicker({
  runtimeId,
  scope,
  onScopeChange,
  currentBranch,
  disabled = false,
}: CompareAgainstPickerProps) {
  const triggerRef = useRef<HTMLButtonElement | null>(null)
  const [open, setOpen] = useState(false)

  // Local "draft" state for typing into the branch field. We only push
  // a new scope to the parent when the user blurs or presses Enter, so
  // intermediate keystrokes don't refetch the changed-files list.
  const initialBranchBase = scope.kind === 'branch' ? scope.base : 'main'
  const [branchDraft, setBranchDraft] = useState(initialBranchBase)
  useEffect(() => {
    if (scope.kind === 'branch') setBranchDraft(scope.base)
  }, [scope])

  // The commit-list fetch is gated on the menu being open AND the user
  // having highlighted commit mode at least once. Until then the stub
  // hook stays parked.
  const [commitMenuVisited, setCommitMenuVisited] = useState(
    scope.kind === 'commit',
  )
  const commitList = useCommitList({
    runtimeId,
    base: branchDraft || 'main',
    enabled: open && commitMenuVisited,
  })

  const activeMode: PickerMode = scope.kind === 'workingTree'
    ? 'workingTree'
    : scope.kind === 'commit'
      ? 'commit'
      : 'branch'

  const triggerLabel = useMemo(() => {
    switch (scope.kind) {
      case 'workingTree':
        return 'Working tree'
      case 'branch':
        return `Branch: ${scope.base}`
      case 'commit':
        return `Commit: ${shortSha(scope.sha)}`
      case 'range':
        return `${shortSha(scope.base)} … ${shortSha(scope.head)}`
    }
  }, [scope])

  const onBase = !!currentBranch && currentBranch === branchDraft
  const baseUnreachable = commitList.isError

  const commitBaseError = baseUnreachable && commitMenuVisited

  const handlePickWorkingTree = () => {
    onScopeChange({ kind: 'workingTree' })
  }

  const handlePickBranch = () => {
    const base = branchDraft.trim() || 'main'
    onScopeChange({ kind: 'branch', base })
  }

  const handlePickCommit = (commit: CommitListItem) => {
    onScopeChange({ kind: 'commit', sha: commit.sha })
    setOpen(false)
  }

  return (
    <>
      <Tooltip title="Compare against" enterDelay={400}>
        <Box
          ref={triggerRef as unknown as React.Ref<HTMLDivElement>}
          component="button"
          type="button"
          onClick={() => !disabled && setOpen((v) => !v)}
          aria-haspopup="menu"
          aria-expanded={open}
          disabled={disabled}
          sx={{
            height: 28,
            px: 1.25,
            display: 'inline-flex',
            alignItems: 'center',
            gap: 0.75,
            borderRadius: '14px',
            backgroundColor: 'rgba(0, 0, 0, 0.03)',
            border: `1px solid ${appContainerTokens.hairline}`,
            cursor: disabled ? 'default' : 'pointer',
            color: appContainerTokens.textMuted,
            transition: 'background-color 200ms ease, border-color 200ms ease, color 200ms ease',
            '&:hover': disabled
              ? undefined
              : {
                  backgroundColor: 'rgba(0, 0, 0, 0.05)',
                  borderColor: appContainerTokens.accent,
                  color: appContainerTokens.textPrimary,
                },
            '&:focus-visible': {
              outline: `2px solid ${appContainerTokens.accent}`,
              outlineOffset: 1,
            },
          }}
        >
          <Typography
            variant="body2"
            component="span"
            sx={{
              color: appContainerTokens.textMuted,
              fontSize: '0.75rem',
              letterSpacing: '-0.005em',
              fontFamily: workspaceFontFamily.mono,
              opacity: 0.85,
            }}
          >
            Compare against
          </Typography>
          <Typography
            variant="body2"
            component="span"
            sx={{
              color: appContainerTokens.textPrimary,
              fontSize: '0.8125rem',
              letterSpacing: '-0.005em',
              fontFamily: workspaceFontFamily.mono,
              maxWidth: 200,
              overflow: 'hidden',
              whiteSpace: 'nowrap',
              textOverflow: 'ellipsis',
            }}
          >
            {triggerLabel}
          </Typography>
          <KeyboardArrowDownIcon
            sx={{
              fontSize: 16,
              color: appContainerTokens.textMuted,
              transform: open ? 'rotate(180deg)' : 'none',
              transition: 'transform 160ms ease',
            }}
          />
        </Box>
      </Tooltip>

      <Popper
        open={open}
        anchorEl={triggerRef.current}
        placement="bottom-start"
        sx={{ zIndex: (t) => t.zIndex.tooltip + 1 }}
      >
        <ClickAwayListener onClickAway={() => setOpen(false)}>
          <Box
            role="menu"
            sx={{
              mt: 0.75,
              width: 360,
              backgroundColor: appContainerTokens.canvasBg,
              border: `1px solid ${appContainerTokens.hairline}`,
              borderRadius: 2,
              boxShadow: '0 12px 32px rgba(0, 0, 0, 0.10)',
              overflow: 'hidden',
            }}
          >
            <PickerRow
              mode="workingTree"
              active={activeMode === 'workingTree'}
              onClick={handlePickWorkingTree}
            >
              <Typography
                variant="body2"
                sx={{
                  color: appContainerTokens.textPrimary,
                  fontSize: '0.8125rem',
                  letterSpacing: '-0.005em',
                }}
              >
                Working tree
              </Typography>
              <Typography
                variant="caption"
                sx={{
                  color: appContainerTokens.textMuted,
                  letterSpacing: '-0.005em',
                  fontSize: '0.7rem',
                  display: 'block',
                  mt: 0.25,
                }}
              >
                Show only uncommitted changes.
              </Typography>
            </PickerRow>

            <Divider />

            <PickerRow
              mode="branch"
              active={activeMode === 'branch'}
              onClick={() => handlePickBranch()}
            >
              <Stack direction="row" spacing={1} alignItems="center">
                <Typography
                  variant="body2"
                  sx={{
                    color: appContainerTokens.textPrimary,
                    fontSize: '0.8125rem',
                    letterSpacing: '-0.005em',
                    minWidth: 60,
                  }}
                >
                  Branch
                </Typography>
                <TextField
                  value={branchDraft}
                  onChange={(e) => setBranchDraft(e.target.value)}
                  onKeyDown={(e) => {
                    if (e.key === 'Enter') {
                      e.preventDefault()
                      handlePickBranch()
                    }
                  }}
                  onBlur={() => {
                    if (activeMode === 'branch') handlePickBranch()
                  }}
                  onClick={(e) => e.stopPropagation()}
                  size="small"
                  variant="outlined"
                  placeholder="main"
                  fullWidth
                  error={commitBaseError}
                  inputProps={{
                    style: { fontFamily: workspaceFontFamily.mono, fontSize: '0.8125rem' },
                  }}
                  sx={{
                    '& .MuiOutlinedInput-root input': { py: 0.5 },
                  }}
                />
              </Stack>
              {onBase && (
                <Typography
                  variant="caption"
                  sx={{
                    display: 'block',
                    mt: 0.5,
                    color: appContainerTokens.textMuted,
                    letterSpacing: '-0.005em',
                    fontSize: '0.7rem',
                  }}
                >
                  You're on the base — nothing to show.
                </Typography>
              )}
              {commitBaseError && (
                <Tooltip title="Couldn't find the base. Fetch from runtime.">
                  <Typography
                    variant="caption"
                    sx={{
                      display: 'block',
                      mt: 0.5,
                      color: 'rgba(178, 64, 64, 0.95)',
                      letterSpacing: '-0.005em',
                      fontSize: '0.7rem',
                    }}
                  >
                    Couldn't find the base.
                  </Typography>
                </Tooltip>
              )}
            </PickerRow>

            <Divider />

            <PickerRow
              mode="commit"
              active={activeMode === 'commit'}
              onClick={() => setCommitMenuVisited(true)}
            >
              <Stack spacing={0.75}>
                <Typography
                  variant="body2"
                  sx={{
                    color: appContainerTokens.textPrimary,
                    fontSize: '0.8125rem',
                    letterSpacing: '-0.005em',
                  }}
                >
                  Commit
                </Typography>
                <CommitList
                  visited={commitMenuVisited}
                  isLoading={commitList.isLoading}
                  isError={commitList.isError}
                  commits={commitList.commits}
                  hasMore={commitList.hasMore}
                  onPick={handlePickCommit}
                  onLoadMore={() => commitList.refetch()}
                />
              </Stack>
            </PickerRow>
          </Box>
        </ClickAwayListener>
      </Popper>
    </>
  )
}

interface PickerRowProps {
  mode: PickerMode
  active: boolean
  onClick: () => void
  children: React.ReactNode
}

function PickerRow({ active, onClick, children }: PickerRowProps) {
  return (
    <Box
      onClick={onClick}
      role="menuitemradio"
      aria-checked={active}
      sx={{
        display: 'flex',
        alignItems: 'flex-start',
        gap: 1,
        px: 1.5,
        py: 1.25,
        cursor: 'pointer',
        backgroundColor: active ? workspaceAccent.soft : 'transparent',
        transition: 'background-color 160ms ease',
        '&:hover': {
          backgroundColor: active
            ? workspaceAccent.soft
            : 'rgba(0, 0, 0, 0.02)',
        },
      }}
    >
      <Radio
        checked={active}
        size="small"
        tabIndex={-1}
        sx={{
          mt: -0.25,
          p: 0.25,
          color: appContainerTokens.textMuted,
          '&.Mui-checked': { color: appContainerTokens.accent },
        }}
      />
      <Box sx={{ flex: 1, minWidth: 0 }}>{children}</Box>
    </Box>
  )
}

function Divider() {
  return (
    <Box
      aria-hidden
      sx={{
        height: 1,
        backgroundColor: appContainerTokens.hairline,
      }}
    />
  )
}

interface CommitListProps {
  visited: boolean
  isLoading: boolean
  isError: boolean
  commits: CommitListItem[]
  hasMore: boolean
  onPick: (commit: CommitListItem) => void
  onLoadMore: () => void
}

function CommitList({
  visited,
  isLoading,
  isError,
  commits,
  hasMore,
  onPick,
  onLoadMore,
}: CommitListProps) {
  if (!visited) {
    return (
      <Typography
        variant="caption"
        sx={{
          color: appContainerTokens.textMuted,
          letterSpacing: '-0.005em',
          fontSize: '0.7rem',
        }}
      >
        Pick a commit on this branch.
      </Typography>
    )
  }

  if (isLoading) {
    return (
      <Stack spacing={0.5} sx={{ py: 0.5 }}>
        {Array.from({ length: 3 }).map((_, i) => (
          <Box
            key={i}
            sx={{
              height: 14,
              backgroundColor: 'rgba(0, 0, 0, 0.04)',
              borderRadius: 0.5,
              animation: 'shimmer 1.4s ease-in-out infinite',
              '@keyframes shimmer': {
                '0%': { opacity: 0.4 },
                '50%': { opacity: 0.7 },
                '100%': { opacity: 0.4 },
              },
            }}
          />
        ))}
        <Stack direction="row" spacing={0.75} alignItems="center" sx={{ mt: 0.5 }}>
          <CircularProgress
            size={12}
            thickness={4}
            sx={{ color: appContainerTokens.textMuted, opacity: 0.7 }}
          />
          <Typography
            variant="caption"
            sx={{
              color: appContainerTokens.textMuted,
              fontSize: '0.7rem',
              letterSpacing: '-0.005em',
            }}
          >
            Loading commits…
          </Typography>
        </Stack>
      </Stack>
    )
  }

  if (isError) {
    return (
      <Typography
        variant="caption"
        sx={{
          color: 'rgba(178, 64, 64, 0.95)',
          letterSpacing: '-0.005em',
          fontSize: '0.7rem',
        }}
      >
        Couldn't find the base.
      </Typography>
    )
  }

  if (commits.length === 0) {
    return (
      <Typography
        variant="caption"
        sx={{
          color: appContainerTokens.textMuted,
          letterSpacing: '-0.005em',
          fontSize: '0.7rem',
        }}
      >
        No commits yet.
      </Typography>
    )
  }

  return (
    <Box
      sx={{
        maxHeight: 260,
        overflowY: 'auto',
        border: `1px solid ${appContainerTokens.hairline}`,
        borderRadius: 1,
        backgroundColor: 'rgba(0, 0, 0, 0.015)',
      }}
    >
      {commits.map((c) => (
        <Box
          key={c.sha}
          onClick={(e) => {
            e.stopPropagation()
            onPick(c)
          }}
          role="menuitem"
          sx={{
            display: 'flex',
            alignItems: 'center',
            gap: 1,
            px: 1,
            py: 0.75,
            cursor: 'pointer',
            borderBottom: `1px solid ${appContainerTokens.hairline}`,
            '&:last-of-type': { borderBottom: 'none' },
            '&:hover': {
              backgroundColor: workspaceAccent.soft,
            },
          }}
        >
          <Typography
            component="span"
            sx={{
              fontFamily: workspaceFontFamily.mono,
              fontSize: '0.7rem',
              color: appContainerTokens.accent,
              minWidth: 56,
            }}
          >
            {shortSha(c.sha)}
          </Typography>
          <Box sx={{ flex: 1, minWidth: 0 }}>
            <Typography
              variant="body2"
              sx={{
                fontSize: '0.75rem',
                color: appContainerTokens.textPrimary,
                letterSpacing: '-0.005em',
                whiteSpace: 'nowrap',
                overflow: 'hidden',
                textOverflow: 'ellipsis',
              }}
            >
              {c.subject}
            </Typography>
            <Typography
              variant="caption"
              sx={{
                fontSize: '0.65rem',
                color: appContainerTokens.textMuted,
                letterSpacing: '-0.005em',
              }}
            >
              {c.author} · {formatRelative(c.authoredAt)}
            </Typography>
          </Box>
        </Box>
      ))}
      {hasMore && (
        <Box
          onClick={(e) => {
            e.stopPropagation()
            onLoadMore()
          }}
          sx={{
            display: 'flex',
            justifyContent: 'center',
            py: 0.75,
            cursor: 'pointer',
            backgroundColor: 'rgba(0, 0, 0, 0.02)',
            '&:hover': { backgroundColor: workspaceAccent.soft },
          }}
        >
          <Typography
            variant="caption"
            sx={{
              color: appContainerTokens.accent,
              fontSize: '0.7rem',
              letterSpacing: '-0.005em',
            }}
          >
            Show more
          </Typography>
        </Box>
      )}
    </Box>
  )
}

function shortSha(sha: string): string {
  return sha.length > 7 ? sha.slice(0, 7) : sha
}

/**
 * Cheap, locale-free relative time formatter. Avoids pulling in a
 * date-fns dep for what's a passing label in a dropdown.
 */
function formatRelative(iso: string): string {
  const t = new Date(iso).getTime()
  if (Number.isNaN(t)) return iso
  const diffSec = Math.max(1, Math.floor((Date.now() - t) / 1000))
  if (diffSec < 60) return `${diffSec}s ago`
  const diffMin = Math.floor(diffSec / 60)
  if (diffMin < 60) return `${diffMin}m ago`
  const diffHr = Math.floor(diffMin / 60)
  if (diffHr < 24) return `${diffHr}h ago`
  const diffDay = Math.floor(diffHr / 24)
  if (diffDay < 30) return `${diffDay}d ago`
  const diffMonth = Math.floor(diffDay / 30)
  if (diffMonth < 12) return `${diffMonth}mo ago`
  const diffYear = Math.floor(diffMonth / 12)
  return `${diffYear}y ago`
}

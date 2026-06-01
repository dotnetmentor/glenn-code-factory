import { useMemo, useState, Fragment } from 'react'
import {
  Alert,
  Box,
  CircularProgress,
  Stack,
  Table,
  TableBody,
  TableCell,
  TableContainer,
  TableHead,
  TableRow,
  Tooltip,
  Typography,
} from '@mui/material'
import KeyboardArrowDownIcon from '@mui/icons-material/KeyboardArrowDown'
import KeyboardArrowRightIcon from '@mui/icons-material/KeyboardArrowRight'
import ReactDiffViewer, { DiffMethod } from 'react-diff-viewer-continued'
import {
  RuntimeProposalStatus,
  useGetApiProjectsProjectIdBranchesBranchIdRuntimeProposals,
  useGetApiProjectsProjectIdProposalsProposalId,
  type RuntimeApplyHistoryItem,
} from '@/api/queries-commands'
import {
  workspaceColors,
  workspaceFontFamily,
  workspaceRuntime,
  workspaceText,
} from '@/applications/workspace/shared/designTokens'
import { formatProposedSpec } from '@/lib/format/formatProposedSpec'

// ── Apply history section ──────────────────────────────────────────────────
//
// Last 20 decided (Applied/Failed) proposals for the branch-scoped runtime,
// rendered as a compact MUI Table. Each row is clickable; expansion fetches
// the full proposal (proposedSpec is on the by-id endpoint but not on the
// history list endpoint) and renders a side-by-side diff against the prior
// successfully-applied spec (i.e. the next Applied row deeper in the list).
//
// We reuse the project's existing diff library — react-diff-viewer-continued,
// already in package.json and already used by SpecTab's DiffSection — so the
// pending-diff and the historical-diff surfaces feel like the same artefact.

export interface ApplyHistorySectionProps {
  projectId: string
  branchId: string
}

export function ApplyHistorySection({ projectId, branchId }: ApplyHistorySectionProps) {
  const historyQuery = useGetApiProjectsProjectIdBranchesBranchIdRuntimeProposals(
    projectId,
    branchId,
    { status: 'Applied,Failed', limit: 20 },
    {
      query: {
        enabled: !!projectId && !!branchId,
      },
    },
  )

  const items = historyQuery.data ?? []

  // Per-proposal-id list of *prior* Applied proposalIds, oldest-first relative
  // to the row's own decidedAt. The diff for row N points at the first Applied
  // row with index > N (next-down-the-list, since the API returns newest-first).
  const priorAppliedByProposalId = useMemo(() => {
    const map = new Map<string, string | null>()
    for (let i = 0; i < items.length; i++) {
      const current = items[i]
      let prior: string | null = null
      for (let j = i + 1; j < items.length; j++) {
        if (items[j].status === RuntimeProposalStatus.Applied) {
          prior = items[j].proposalId
          break
        }
      }
      map.set(current.proposalId, prior)
    }
    return map
  }, [items])

  const [expanded, setExpanded] = useState<Set<string>>(() => new Set())
  const toggleExpanded = (proposalId: string) => {
    setExpanded((prev) => {
      const next = new Set(prev)
      if (next.has(proposalId)) next.delete(proposalId)
      else next.add(proposalId)
      return next
    })
  }

  if (historyQuery.isLoading) {
    return (
      <Section title="Apply history">
        <Stack direction="row" spacing={1} alignItems="center" sx={{ py: 1 }}>
          <CircularProgress size={14} sx={{ color: workspaceText.muted }} />
          <Typography sx={{ fontSize: 12.5, color: workspaceText.muted }}>
            Loading apply history…
          </Typography>
        </Stack>
      </Section>
    )
  }

  if (historyQuery.isError) {
    return (
      <Section title="Apply history">
        <Alert severity="error" sx={{ fontSize: 12.5 }}>
          Failed to load apply history.
        </Alert>
      </Section>
    )
  }

  if (items.length === 0) {
    return (
      <Section title="Apply history">
        <Typography sx={{ fontSize: 12.5, color: workspaceText.muted }}>
          No applied or failed proposals yet.
        </Typography>
      </Section>
    )
  }

  return (
    <Section title="Apply history" subtitle={`${items.length} decided proposal${items.length === 1 ? '' : 's'}`}>
      <TableContainer
        sx={{
          border: `1px solid ${workspaceColors.hairline}`,
          borderRadius: 1,
          backgroundColor: workspaceColors.canvasBg,
          '& .MuiTableCell-root': {
            borderBottom: `1px solid ${workspaceColors.hairline}`,
            py: 1,
            fontSize: 12.5,
            letterSpacing: '-0.005em',
          },
          '& .MuiTableHead-root .MuiTableCell-root': {
            color: workspaceText.muted,
            fontSize: '0.6875rem',
            fontWeight: 600,
            letterSpacing: '0.08em',
            textTransform: 'uppercase',
          },
          '& tr:last-of-type .MuiTableCell-root': { borderBottom: 'none' },
        }}
      >
        <Table size="small" aria-label="Apply history">
          <TableHead>
            <TableRow>
              <TableCell sx={{ width: 36 }} />
              <TableCell>Proposal</TableCell>
              <TableCell>Decided by</TableCell>
              <TableCell>When</TableCell>
              <TableCell>Status</TableCell>
              <TableCell align="right">Total</TableCell>
              <TableCell>Phases</TableCell>
            </TableRow>
          </TableHead>
          <TableBody>
            {items.map((row) => {
              const isExpanded = expanded.has(row.proposalId)
              const priorId = priorAppliedByProposalId.get(row.proposalId) ?? null
              return (
                <Fragment key={row.proposalId}>
                  <TableRow
                    onClick={() => toggleExpanded(row.proposalId)}
                    sx={{
                      cursor: 'pointer',
                      transition: 'background-color 120ms ease',
                      '&:hover': { backgroundColor: workspaceColors.chipBg },
                    }}
                  >
                    <TableCell sx={{ width: 36, pr: 0 }}>
                      {isExpanded ? (
                        <KeyboardArrowDownIcon
                          fontSize="small"
                          sx={{ color: workspaceText.muted }}
                        />
                      ) : (
                        <KeyboardArrowRightIcon
                          fontSize="small"
                          sx={{ color: workspaceText.muted }}
                        />
                      )}
                    </TableCell>
                    <TableCell>
                      <Tooltip title={row.proposalId}>
                        <Typography
                          sx={{
                            fontFamily: workspaceFontFamily.mono,
                            fontSize: 12,
                            color: workspaceText.primary,
                          }}
                        >
                          {shortProposalId(row.proposalId)}
                        </Typography>
                      </Tooltip>
                    </TableCell>
                    <TableCell>
                      <Typography
                        sx={{
                          fontSize: 12.5,
                          color: workspaceText.primary,
                          maxWidth: 160,
                          overflow: 'hidden',
                          textOverflow: 'ellipsis',
                          whiteSpace: 'nowrap',
                        }}
                        title={row.decidedBy ?? undefined}
                      >
                        {formatDecidedBy(row.decidedBy)}
                      </Typography>
                    </TableCell>
                    <TableCell>
                      <Tooltip title={row.decidedAt ? new Date(row.decidedAt).toLocaleString() : ''}>
                        <Typography
                          sx={{
                            fontSize: 12.5,
                            color: workspaceText.muted,
                          }}
                        >
                          {formatRelativeShort(row.decidedAt)}
                        </Typography>
                      </Tooltip>
                    </TableCell>
                    <TableCell>
                      <StatusBadge status={row.status} />
                    </TableCell>
                    <TableCell align="right">
                      <Typography
                        sx={{
                          fontSize: 12,
                          fontFamily: workspaceFontFamily.mono,
                          color: workspaceText.primary,
                        }}
                      >
                        {formatApplyMs(row.totalApplyMs)}
                      </Typography>
                    </TableCell>
                    <TableCell>
                      <Typography
                        sx={{
                          fontSize: 11.5,
                          fontFamily: workspaceFontFamily.mono,
                          color: workspaceText.muted,
                          maxWidth: 220,
                          overflow: 'hidden',
                          textOverflow: 'ellipsis',
                          whiteSpace: 'nowrap',
                        }}
                        title={formatPhaseTimingsFull(row.phaseTimings)}
                      >
                        {formatPhaseTimingsShort(row.phaseTimings)}
                      </Typography>
                    </TableCell>
                  </TableRow>
                  {row.status === RuntimeProposalStatus.Failed && row.errorMessage && (
                    <TableRow sx={{ '& .MuiTableCell-root': { pt: 0 } }}>
                      <TableCell />
                      <TableCell colSpan={6}>
                        <Tooltip title={row.errorMessage}>
                          <Typography
                            sx={{
                              fontFamily: workspaceFontFamily.mono,
                              fontSize: 11.5,
                              color: workspaceRuntime.failed,
                              maxWidth: '100%',
                              overflow: 'hidden',
                              textOverflow: 'ellipsis',
                              whiteSpace: 'nowrap',
                            }}
                          >
                            {truncate(row.errorMessage, 120)}
                          </Typography>
                        </Tooltip>
                      </TableCell>
                    </TableRow>
                  )}
                  {isExpanded && (
                    <TableRow>
                      <TableCell />
                      <TableCell colSpan={6} sx={{ p: 0 }}>
                        <Box sx={{ p: 1.5 }}>
                          <ApplyHistoryDiff
                            projectId={projectId}
                            proposal={row}
                            priorProposalId={priorId}
                          />
                        </Box>
                      </TableCell>
                    </TableRow>
                  )}
                </Fragment>
              )
            })}
          </TableBody>
        </Table>
      </TableContainer>
    </Section>
  )
}

interface ApplyHistoryDiffProps {
  projectId: string
  proposal: RuntimeApplyHistoryItem
  /** ProposalId of the previous Applied proposal to diff against, or null. */
  priorProposalId: string | null
}

/**
 * Loads both the row's proposal (for {@code proposedSpec}) and the prior
 * Applied row's proposal (for the comparison base) and renders a side-by-side
 * diff. Either side can be missing — a failed-only chain has no prior Applied
 * row, and the by-id endpoint can fail independently. The component degrades
 * gracefully in each case rather than blanking out the expander.
 */
function ApplyHistoryDiff({ projectId, proposal, priorProposalId }: ApplyHistoryDiffProps) {
  const currentQuery = useGetApiProjectsProjectIdProposalsProposalId(
    projectId,
    proposal.proposalId,
    { query: { enabled: !!projectId && !!proposal.proposalId } },
  )
  const priorQuery = useGetApiProjectsProjectIdProposalsProposalId(
    projectId,
    priorProposalId ?? '',
    { query: { enabled: !!projectId && !!priorProposalId } },
  )

  const currentJson = useMemo(
    () => formatProposedSpec(currentQuery.data?.proposedSpec ?? ''),
    [currentQuery.data?.proposedSpec],
  )
  const priorJson = useMemo(
    () => formatProposedSpec(priorQuery.data?.proposedSpec ?? ''),
    [priorQuery.data?.proposedSpec],
  )

  if (currentQuery.isLoading || (priorProposalId && priorQuery.isLoading)) {
    return (
      <Stack direction="row" spacing={1} alignItems="center">
        <CircularProgress size={14} sx={{ color: workspaceText.muted }} />
        <Typography sx={{ fontSize: 12, color: workspaceText.muted }}>
          Loading spec diff…
        </Typography>
      </Stack>
    )
  }

  // Failed proposal with no prior Applied — render the failed spec full-width
  // so the operator can still inspect what was attempted.
  if (!priorProposalId && currentJson) {
    return (
      <Box>
        <Typography
          sx={{
            fontSize: 11.5,
            color: workspaceText.muted,
            mb: 0.75,
          }}
        >
          No prior applied spec to diff against — showing the proposed spec
          for this attempt.
        </Typography>
        <CodeBlock>{currentJson}</CodeBlock>
      </Box>
    )
  }

  if (!currentJson && !priorJson) {
    return (
      <Typography sx={{ fontSize: 12, color: workspaceText.muted }}>
        Spec content unavailable for this proposal.
      </Typography>
    )
  }

  return (
    <Box
      sx={{
        border: `1px solid ${workspaceColors.hairline}`,
        borderRadius: 1,
        overflow: 'hidden',
        backgroundColor: workspaceColors.canvasBg,
        '& pre, & td.diff-cell, & div': {
          fontFamily: workspaceFontFamily.mono,
        },
        fontSize: 12.5,
      }}
    >
      <ReactDiffViewer
        oldValue={priorJson}
        newValue={currentJson}
        splitView
        compareMethod={DiffMethod.LINES}
        leftTitle="Prior applied"
        rightTitle="This attempt"
        useDarkTheme={false}
        styles={MUTED_DIFF_STYLES}
      />
    </Box>
  )
}

function StatusBadge({ status }: { status: RuntimeProposalStatus }) {
  const isApplied = status === RuntimeProposalStatus.Applied
  const isFailed = status === RuntimeProposalStatus.Failed
  const tone = isApplied
    ? { color: '#3E6B47', bg: 'rgba(52, 199, 89, 0.12)', border: 'rgba(52, 199, 89, 0.32)' }
    : isFailed
    ? { color: workspaceRuntime.failed, bg: 'rgba(255, 59, 48, 0.10)', border: 'rgba(255, 59, 48, 0.30)' }
    : { color: workspaceText.muted, bg: workspaceColors.chipBg, border: workspaceColors.hairline }
  return (
    <Box
      component="span"
      sx={{
        fontSize: '0.6875rem',
        fontWeight: 600,
        letterSpacing: '0.04em',
        textTransform: 'uppercase',
        color: tone.color,
        bgcolor: tone.bg,
        border: `1px solid ${tone.border}`,
        borderRadius: 999,
        px: 0.875,
        py: 0.125,
      }}
    >
      {String(status)}
    </Box>
  )
}

function shortProposalId(proposalId: string): string {
  if (!proposalId) return '—'
  return `${proposalId.slice(0, 8)}…`
}

function formatDecidedBy(decidedBy: string | null | undefined): string {
  if (!decidedBy) return 'unknown'
  // If it looks like a UUID, truncate for readability — usernames / emails
  // pass through as-is.
  if (/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(decidedBy)) {
    return `${decidedBy.slice(0, 8)}…`
  }
  return decidedBy
}

function formatApplyMs(ms: number | null | undefined): string {
  if (ms == null || !Number.isFinite(ms) || ms < 0) return '—'
  if (ms >= 1000) return `${(ms / 1000).toFixed(ms >= 10_000 ? 0 : 1)}s`
  return `${Math.round(ms)}ms`
}

/**
 * The backend serialises phaseTimings as a JSON string. Parse defensively —
 * an unexpected shape should degrade to "no phase data" rather than throw
 * from the render pass. Inline string form: "install 1.2s · services 3.1s".
 */
function parsePhaseTimings(raw: string | null | undefined): Array<[string, number]> {
  if (!raw) return []
  try {
    const parsed = JSON.parse(raw) as Record<string, unknown>
    if (!parsed || typeof parsed !== 'object') return []
    const out: Array<[string, number]> = []
    for (const [key, value] of Object.entries(parsed)) {
      if (typeof value === 'number' && Number.isFinite(value)) {
        out.push([key, value])
      }
    }
    return out
  } catch {
    return []
  }
}

function formatPhaseTimingsShort(raw: string | null | undefined): string {
  const phases = parsePhaseTimings(raw)
  if (phases.length === 0) return '—'
  return phases.map(([k, v]) => `${k} ${formatApplyMs(v)}`).join(' · ')
}

function formatPhaseTimingsFull(raw: string | null | undefined): string {
  const phases = parsePhaseTimings(raw)
  if (phases.length === 0) return ''
  return phases.map(([k, v]) => `${k}: ${formatApplyMs(v)}`).join('\n')
}

function formatRelativeShort(value: string | null | undefined): string {
  if (!value) return '—'
  const ts = new Date(value)
  if (Number.isNaN(ts.getTime())) return '—'
  const diffSec = Math.max(0, Math.floor((Date.now() - ts.getTime()) / 1000))
  if (diffSec < 60) return `${diffSec}s ago`
  const diffMin = Math.floor(diffSec / 60)
  if (diffMin < 60) return `${diffMin}m ago`
  const diffHr = Math.floor(diffMin / 60)
  if (diffHr < 24) return `${diffHr}h ago`
  const diffDay = Math.floor(diffHr / 24)
  if (diffDay === 1) return 'yesterday'
  if (diffDay < 7) return `${diffDay}d ago`
  return ts.toLocaleDateString()
}

function truncate(value: string, max: number): string {
  if (value.length <= max) return value
  return `${value.slice(0, max - 1)}…`
}

interface SectionProps {
  title: React.ReactNode
  subtitle?: React.ReactNode
  children: React.ReactNode
}

function Section({ title, subtitle, children }: SectionProps) {
  return (
    <Stack spacing={1} sx={{ mb: 2.5 }}>
      <Box>
        <Typography
          component="div"
          sx={{
            fontSize: '0.6875rem',
            fontWeight: 600,
            letterSpacing: '0.08em',
            textTransform: 'uppercase',
            color: workspaceText.muted,
          }}
        >
          {title}
        </Typography>
        {typeof subtitle === 'string' ? (
          <Typography
            sx={{
              fontSize: 12,
              color: workspaceText.faint,
              display: 'block',
              mt: 0.25,
            }}
          >
            {subtitle}
          </Typography>
        ) : subtitle ? (
          <Box sx={{ mt: 0.5 }}>{subtitle}</Box>
        ) : null}
      </Box>
      {children}
    </Stack>
  )
}

function CodeBlock({ children }: { children: string }) {
  return (
    <Box
      component="pre"
      sx={{
        m: 0,
        p: 2,
        bgcolor: workspaceColors.canvasBg,
        color: workspaceText.primary,
        border: `1px solid ${workspaceColors.hairline}`,
        borderRadius: 1,
        fontFamily: workspaceFontFamily.mono,
        fontSize: 12.5,
        lineHeight: 1.55,
        whiteSpace: 'pre-wrap',
        wordBreak: 'break-word',
        maxHeight: 480,
        overflowY: 'auto',
      }}
    >
      {children}
    </Box>
  )
}

/**
 * Muted palette for the diff viewer — matches the SpecEditorDialog confirm
 * step so both surfaces feel like the same workspace artefact.
 */
const MUTED_DIFF_STYLES = {
  variables: {
    light: {
      diffViewerBackground: workspaceColors.canvasBg,
      diffViewerColor: workspaceText.primary,
      addedBackground: 'rgba(127, 178, 87, 0.08)',
      addedColor: workspaceText.primary,
      removedBackground: 'rgba(178, 84, 56, 0.06)',
      removedColor: workspaceText.primary,
      wordAddedBackground: 'rgba(127, 178, 87, 0.22)',
      wordRemovedBackground: 'rgba(178, 84, 56, 0.18)',
      addedGutterBackground: 'rgba(127, 178, 87, 0.12)',
      removedGutterBackground: 'rgba(178, 84, 56, 0.08)',
      gutterBackground: workspaceColors.chipBg,
      gutterBackgroundDark: workspaceColors.chromeBg,
      highlightBackground: workspaceColors.chipHoverBg,
      highlightGutterBackground: workspaceColors.chipHoverBg,
      codeFoldGutterBackground: workspaceColors.chromeBg,
      codeFoldBackground: workspaceColors.chromeBg,
      emptyLineBackground: 'transparent',
      gutterColor: workspaceText.faint,
      addedGutterColor: workspaceText.muted,
      removedGutterColor: workspaceText.muted,
      codeFoldContentColor: workspaceText.muted,
      diffViewerTitleBackground: workspaceColors.chromeBg,
      diffViewerTitleColor: workspaceText.muted,
      diffViewerTitleBorderColor: workspaceColors.hairline,
    },
  },
  contentText: {
    fontFamily: workspaceFontFamily.mono,
  },
} as const

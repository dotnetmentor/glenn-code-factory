import { useEffect, useMemo, useRef, useState } from 'react'
import {
  Box,
  Button,
  CircularProgress,
  Divider,
  Popover,
  Stack,
  Typography,
} from '@mui/material'
import WarningAmberIcon from '@mui/icons-material/WarningAmber'
import AutoFixHighIcon from '@mui/icons-material/AutoFixHigh'
import {
  RuntimeSpecHealth,
  type RuntimeBootIssueDto,
  type RuntimeStatusResponse,
} from '@/api/queries-commands'
import { workspaceRuntime } from '@/applications/workspace/shared/designTokens'

export interface RuntimeDegradedBannerProps {
  /**
   * Branch-scoped runtime status row. The banner reads {@code specHealth} and
   * {@code recentBootIssues} off this object; it only renders when
   * {@code specHealth === 'Degraded'}. Undefined / loading status keeps the
   * banner hidden.
   */
  status: RuntimeStatusResponse | undefined
  /**
   * Fired when the operator clicks "Let agent fix it". The parent owns the
   * repair mutation (it has the runtime id + the query key to invalidate on
   * success) — this component is purely presentational about it.
   */
  onRepair: () => void
  /**
   * True while the repair mutation is in flight. Disables the button and
   * swaps the label to "Agent is working on it…".
   */
  isRepairing: boolean
}

/**
 * A single parsed boot issue, normalized from the JSON {@code payload} string
 * the daemon emits on each {@code SpecDegraded} event. We defensively try the
 * structured shape ({@code stage}/{@code service}/{@code reason}/{@code detail})
 * and fall back to the raw payload string when it doesn't parse.
 */
interface ParsedBootIssue {
  key: string
  stage?: string
  service?: string
  reason?: string
  detail?: string
  /** Raw payload, surfaced when structured parsing yields nothing useful. */
  raw?: string
  severity: string
  timestamp: string
}

/**
 * Best-effort parse of a {@link RuntimeBootIssueDto}. The {@code payload} is a
 * JSON string produced server-side; bad / unexpected JSON must never crash the
 * banner, so anything that doesn't parse falls back to the raw string.
 */
function parseBootIssue(issue: RuntimeBootIssueDto, index: number): ParsedBootIssue {
  const base = {
    key: `${issue.timestamp}-${index}`,
    severity: issue.severity,
    timestamp: issue.timestamp,
  }
  try {
    const parsed = JSON.parse(issue.payload) as Record<string, unknown>
    const stage = typeof parsed.stage === 'string' ? parsed.stage : undefined
    const service = typeof parsed.service === 'string' ? parsed.service : undefined
    const reason = typeof parsed.reason === 'string' ? parsed.reason : undefined
    const detail = typeof parsed.detail === 'string' ? parsed.detail : undefined
    if (stage || service || reason || detail) {
      return { ...base, stage, service, reason, detail }
    }
    // Parsed to an object but none of the known fields — show the raw JSON so
    // the operator at least sees what the daemon emitted.
    return { ...base, raw: issue.payload }
  } catch {
    return { ...base, raw: issue.payload }
  }
}

/**
 * One-line headline for a parsed issue, used in the collapsed summary count and
 * as the popover row title.
 */
function issueHeadline(issue: ParsedBootIssue): string {
  const stage = issue.stage ?? 'Boot'
  if (issue.service) return `${stage} · ${issue.service}`
  return stage
}

/**
 * Amber "Degraded spec" warning banner. Renders only when the runtime reached
 * Online but its spec did not fully apply ({@code specHealth === 'Degraded'}).
 *
 * <p>Mirrors the {@code RuntimeFlyDriftDetected} drift-banner visual language
 * already used in {@link RuntimeStatusHeader}: an amber strip with a
 * {@link WarningAmberIcon}, a clickable summary that opens a popover listing the
 * parsed boot issues, plus a "Let agent fix it" repair button. While the repair
 * is dispatching the button disables and reads "Agent is working on it…"; once
 * the spec heals back to {@code Healthy} the parent's status query no longer
 * reports Degraded and the banner unmounts.</p>
 */
export function RuntimeDegradedBanner({
  status,
  onRepair,
  isRepairing,
}: RuntimeDegradedBannerProps) {
  const issues = useMemo(() => {
    const raw = status?.recentBootIssues ?? []
    return raw.map((issue, i) => parseBootIssue(issue, i))
  }, [status?.recentBootIssues])

  const anchorRef = useRef<HTMLButtonElement | null>(null)
  const [popoverOpen, setPopoverOpen] = useState(false)

  const degraded = status?.specHealth === RuntimeSpecHealth.Degraded

  // Close the popover if the banner heals away while it's still open.
  useEffect(() => {
    if (!degraded && popoverOpen) setPopoverOpen(false)
  }, [degraded, popoverOpen])

  if (!degraded) return null

  const issueCount = issues.length
  const summary =
    issueCount > 0
      ? `${issueCount} boot ${issueCount === 1 ? 'issue' : 'issues'} — click for details`
      : 'The agent can diagnose and re-apply the spec.'

  return (
    <Box
      sx={{
        display: 'flex',
        alignItems: 'center',
        gap: 1,
        width: '100%',
        px: 2,
        py: 1,
        // Warning tint — same semantic orange as the drift banner.
        backgroundColor: 'rgba(255, 149, 0, 0.10)',
        borderLeft: `3px solid ${workspaceRuntime.booting}`,
        borderBottom: '1px solid rgba(255, 149, 0, 0.30)',
        color: workspaceRuntime.booting,
      }}
    >
      <WarningAmberIcon sx={{ fontSize: 18, color: workspaceRuntime.booting, flexShrink: 0 }} />
      <Box
        component="button"
        type="button"
        ref={anchorRef}
        onClick={() => issueCount > 0 && setPopoverOpen((v) => !v)}
        aria-label="Show spec boot issue details"
        disabled={issueCount === 0}
        sx={{
          flex: 1,
          minWidth: 0,
          display: 'flex',
          flexDirection: 'column',
          gap: 0.25,
          border: 0,
          background: 'transparent',
          textAlign: 'left',
          p: 0,
          color: 'inherit',
          cursor: issueCount > 0 ? 'pointer' : 'default',
          '&:focus-visible': {
            outline: `2px solid ${workspaceRuntime.booting}`,
            outlineOffset: 2,
            borderRadius: 1,
          },
        }}
      >
        <Typography
          variant="body2"
          sx={{ fontWeight: 600, color: workspaceRuntime.booting, lineHeight: 1.3 }}
        >
          Runtime started, but the spec didn't fully apply
        </Typography>
        <Typography
          variant="caption"
          sx={{
            color: 'rgba(122, 85, 39, 0.85)',
            overflow: 'hidden',
            textOverflow: 'ellipsis',
            whiteSpace: 'nowrap',
          }}
        >
          {summary}
        </Typography>
      </Box>
      <Button
        size="small"
        variant="contained"
        disableElevation
        onClick={onRepair}
        disabled={isRepairing}
        startIcon={
          isRepairing ? (
            <CircularProgress size={14} color="inherit" />
          ) : (
            <AutoFixHighIcon sx={{ fontSize: 16 }} />
          )
        }
        sx={{
          flexShrink: 0,
          textTransform: 'none',
          fontWeight: 600,
          backgroundColor: workspaceRuntime.booting,
          color: '#fff',
          '&:hover': { backgroundColor: workspaceRuntime.booting, filter: 'brightness(0.94)' },
          '&.Mui-disabled': {
            backgroundColor: 'rgba(255, 149, 0, 0.45)',
            color: '#fff',
          },
        }}
      >
        {isRepairing ? 'Agent is working on it…' : 'Let agent fix it'}
      </Button>
      <Popover
        open={popoverOpen && issueCount > 0}
        anchorEl={anchorRef.current}
        onClose={() => setPopoverOpen(false)}
        anchorOrigin={{ vertical: 'bottom', horizontal: 'left' }}
        transformOrigin={{ vertical: 'top', horizontal: 'left' }}
        PaperProps={{
          sx: { maxWidth: 560, maxHeight: 360, overflow: 'auto', p: 1.5 },
        }}
      >
        <Typography
          variant="caption"
          sx={{ display: 'block', mb: 1, color: 'text.secondary' }}
        >
          Spec boot issues
        </Typography>
        <Stack divider={<Divider flexItem />} spacing={1}>
          {issues.map((issue) => (
            <Box key={issue.key}>
              <Typography variant="subtitle2" sx={{ fontSize: '0.8125rem' }}>
                {issueHeadline(issue)}
              </Typography>
              {issue.reason && (
                <Typography variant="body2" color="text.secondary">
                  {issue.reason}
                </Typography>
              )}
              {issue.detail && (
                <Box
                  component="pre"
                  sx={{
                    m: 0,
                    mt: 0.5,
                    fontSize: '0.7rem',
                    lineHeight: 1.5,
                    whiteSpace: 'pre-wrap',
                    wordBreak: 'break-word',
                    color: 'text.secondary',
                    fontFamily:
                      'ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, monospace',
                  }}
                >
                  {issue.detail}
                </Box>
              )}
              {issue.raw && (
                <Box
                  component="pre"
                  sx={{
                    m: 0,
                    mt: 0.5,
                    fontSize: '0.7rem',
                    lineHeight: 1.5,
                    whiteSpace: 'pre-wrap',
                    wordBreak: 'break-word',
                    color: 'text.secondary',
                    fontFamily:
                      'ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, monospace',
                  }}
                >
                  {issue.raw}
                </Box>
              )}
            </Box>
          ))}
        </Stack>
      </Popover>
    </Box>
  )
}

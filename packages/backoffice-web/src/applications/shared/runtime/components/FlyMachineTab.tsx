import { useCallback, useMemo, useState } from 'react'
import {
  Alert,
  Box,
  CircularProgress,
  IconButton,
  Stack,
  Tooltip,
  Typography,
} from '@mui/material'
import { keyframes } from '@mui/system'
import type { SvgIconComponent } from '@mui/icons-material'
import RefreshIcon from '@mui/icons-material/Refresh'
import CompareArrowsOutlinedIcon from '@mui/icons-material/CompareArrowsOutlined'
import HistoryOutlinedIcon from '@mui/icons-material/HistoryOutlined'
import {
  useGetApiAdminRuntimesRuntimeIdFlySnapshot,
  useGetApiProjectsProjectIdBranchesBranchIdRuntimeStatus,
  type FlyOperationView,
  type FlySnapshotResponse,
} from '@/api/queries-commands'
import {
  monoNumberSx,
  semanticTokens,
  workspaceAccent,
  workspaceFontFamily,
  workspaceText,
} from '@/applications/workspace/shared/designTokens'
import { IconTile, IdChip } from '@/applications/workspace/shared/primitives'

const DASH = '\u2014'

/**
 * Map a raw Fly operation status string onto a semantic dot color. These come
 * off the backend as {@code Succeeded / Failed / TimedOut / Pending} — not the
 * RuntimeState enum the shared StatusDot maps — so we resolve them here against
 * the token palette to stay consistent with the rest of the restyled panel.
 */
function operationStatusColor(status: string): string {
  switch (status) {
    case 'Succeeded':
      return semanticTokens.success
    case 'Failed':
      return semanticTokens.error
    case 'TimedOut':
      return semanticTokens.warning
    case 'Pending':
      return semanticTokens.suspended
    default:
      return workspaceText.muted
  }
}

/** Subtle warning tint used to flag a state-drift row in the comparison grid. */
const AMBER_TINT_BG = semanticTokens.warningSoft
const AMBER_DOT = semanticTokens.warning

/** Breathing pulse for transitional/in-flight status dots — mirrors StatusDot. */
const pulseKeyframes = keyframes`
  0%, 100% { opacity: 0.55; }
  50%      { opacity: 1; }
`

export interface FlyMachineTabProps {
  projectId: string
  /**
   * Branch the user is currently looking at. Required for resolving the
   * correct {@code runtimeId} — a project has one ProjectRuntime per branch,
   * so resolving by projectId alone would surface a sibling branch's runtime
   * (the root cause of the "DB / Fly / UI show different things" report).
   */
  branchId: string
  /** Whether this view is currently active — drives the refetch enabling. */
  active: boolean
  /** Whether this view should be allowed to render at all. */
  isSuperAdmin: boolean
}

/**
 * Superadmin-only fourth tab in the runtime debug panel. Renders the
 * canonical "our view ↔ Fly's view" comparison plus a list of recent
 * Fly API operations so an operator can eyeball drift and the last few
 * machine commands without leaving the project workspace.
 *
 * <p>Fetching is gated on three booleans: a real {@code runtimeId} for
 * the current branch, the calling user holding {@link ApplicationRoles.SuperAdmin},
 * and this view being the active panel surface. Polls every 15 seconds while
 * those hold, otherwise idles.</p>
 *
 * <p><b>Why the branch-aware status query.</b> The earlier
 * {@code useGetApiProjectsProjectIdRuntimeSpec} resolver returned the
 * project's newest ProjectRuntime regardless of which branch the user was
 * on — which meant the Fly tab showed the snapshot of a sibling branch's
 * machine. The branch-aware status endpoint filters by both projectId AND
 * branchId so this tab always pairs with the runtime row the user is
 * actually looking at.</p>
 */
export function FlyMachineTab({ projectId, branchId, active, isSuperAdmin }: FlyMachineTabProps) {
  const statusQuery = useGetApiProjectsProjectIdBranchesBranchIdRuntimeStatus(
    projectId,
    branchId,
    {
      query: { enabled: !!projectId && !!branchId && isSuperAdmin },
    },
  )
  const runtimeId = statusQuery.data?.runtimeId

  const snapshotQuery = useGetApiAdminRuntimesRuntimeIdFlySnapshot(runtimeId ?? '', {
    query: {
      enabled: !!runtimeId && isSuperAdmin && active,
      refetchInterval: 15_000,
    },
  })

  const handleRefresh = useCallback(() => {
    void snapshotQuery.refetch()
  }, [snapshotQuery])

  if (!isSuperAdmin) {
    // Belt-and-braces — the SegmentedSwitcher should never render this tab
    // for non-superadmins, but if someone deep-links to a localStorage view
    // they shouldn't see anything sensitive.
    return null
  }

  if (statusQuery.isLoading) {
    return <CenteredHint label="Loading runtime…" />
  }

  if (!runtimeId) {
    return <CenteredHint label="No runtime for this branch." muted />
  }

  if (snapshotQuery.isLoading && !snapshotQuery.data) {
    return <CenteredHint label="Loading Fly snapshot…" />
  }

  if (snapshotQuery.isError && !snapshotQuery.data) {
    return (
      <Box sx={{ p: 2 }}>
        <Alert
          severity="error"
          variant="outlined"
          action={
            <IconButton size="small" onClick={handleRefresh} aria-label="Retry">
              <RefreshIcon sx={{ fontSize: 16 }} />
            </IconButton>
          }
        >
          Failed to load Fly snapshot.
        </Alert>
      </Box>
    )
  }

  const data = snapshotQuery.data
  if (!data) {
    return <CenteredHint label="No snapshot data." muted />
  }

  return (
    <Box
      sx={{
        height: '100%',
        minHeight: 0,
        overflowY: 'auto',
        px: 2,
        py: 1.5,
        backgroundColor: 'instrument.canvas',
      }}
    >
      <Stack spacing={2.5}>
        <HeaderStrip
          generatedAt={data.generatedAt}
          isFetching={snapshotQuery.isFetching}
          onRefresh={handleRefresh}
        />
        <ComparisonSection data={data} />
        <OperationsSection operations={data.recentOperations ?? []} />
      </Stack>
    </Box>
  )
}

// ── Header strip ────────────────────────────────────────────────────────────

interface HeaderStripProps {
  generatedAt: string
  isFetching: boolean
  onRefresh: () => void
}

function HeaderStrip({ generatedAt, isFetching, onRefresh }: HeaderStripProps) {
  return (
    <Stack direction="row" alignItems="center" spacing={1.5}>
      <Eyebrow>Fly machine</Eyebrow>
      <Box sx={{ flex: 1 }} />
      <Typography
        variant="caption"
        sx={{
          fontSize: '0.75rem',
          color: workspaceText.muted,
          letterSpacing: '-0.005em',
        }}
      >
        Updated <TimeAgo iso={generatedAt} />
      </Typography>
      <Tooltip title="Refresh" enterDelay={400}>
        <span>
          <IconButton
            size="small"
            onClick={onRefresh}
            disabled={isFetching}
            aria-label="Refresh Fly snapshot"
            sx={{
              color: workspaceText.muted,
              p: 0.625,
              '&:hover': {
                color: workspaceText.primary,
                backgroundColor: 'instrument.chipHoverBg',
              },
            }}
          >
            {isFetching ? (
              <CircularProgress size={14} sx={{ color: workspaceText.muted }} />
            ) : (
              <RefreshIcon sx={{ fontSize: 16 }} />
            )}
          </IconButton>
        </span>
      </Tooltip>
    </Stack>
  )
}

// ── Comparison section ──────────────────────────────────────────────────────

interface ComparisonSectionProps {
  data: FlySnapshotResponse
}

/**
 * Map ourView.state ↔ flyView.state pairs that should be considered in
 * agreement. Anything outside this whitelist (when both sides are present
 * and meaningful) renders as a drift row.
 */
const COMPATIBLE_STATE_PAIRS: ReadonlyArray<readonly [string, string]> = [
  ['Online', 'started'],
  ['Booting', 'starting'],
  ['Booting', 'started'],
  // Bootstrapping is the post-Fly-up, pre-daemon-ready window — the machine
  // is fully started on Fly's side while our supervisor still does the tarball
  // download + service-up dance. Listed here so the comparison panel doesn't
  // amber-flag the legitimate happy path.
  ['Bootstrapping', 'started'],
  ['Bootstrapping', 'starting'],
  // Waking is the mirror of Bootstrapping for the suspend→resume path: Fly
  // has already moved the machine back to started but the daemon hasn't
  // re-handshaked yet. Same legitimate-mid-state reasoning.
  ['Waking', 'starting'],
  ['Waking', 'started'],
  ['Suspended', 'suspended'],
  ['Suspended', 'stopped'],
  ['Suspending', 'suspended'],
  ['Suspending', 'stopped'],
  // Suspending + started is the brief window between the DB flip and the Fly
  // StopMachine round-trip completing. ArchiveBranchHandler/IdlerJob both
  // issue StopMachine immediately after the transition, and
  // RuntimeReconcilerJob retries on drift, so this state should be very
  // short-lived — surface it as agreement rather than scaring an operator.
  ['Suspending', 'started'],
  ['Pending', 'created'],
  ['Pending', 'starting'],
  ['Deleting', 'destroying'],
  ['Deleted', 'destroyed'],
]

function statesAgree(ours: string | null | undefined, fly: string | null | undefined): boolean {
  if (!ours || !fly) return true // can't disagree if one side is unknown
  const oursL = ours.toLowerCase()
  const flyL = fly.toLowerCase()
  return COMPATIBLE_STATE_PAIRS.some(
    ([o, f]) => o.toLowerCase() === oursL && f.toLowerCase() === flyL,
  )
}

function ComparisonSection({ data }: ComparisonSectionProps) {
  const { ourView, flyView } = data
  // Defensive null check — Swashbuckle quirk makes Orval type flyView as
  // non-null even though the backend returns null when the machine isn't found.
  const flyPresent = flyView != null

  const stateDrift = flyPresent && !statesAgree(ourView.state, flyView.state)
  const regionDrift = flyPresent && !!ourView.region && !!flyView.region &&
    ourView.region.toLowerCase() !== flyView.region.toLowerCase()
  const machineIdMissing = !ourView.flyMachineId && ourView.state !== 'Pending'

  return (
    <Box>
      <SectionHeader icon={CompareArrowsOutlinedIcon} label="Our view vs Fly" />
      <Box
        sx={{
          mt: 1,
          border: 1,
          borderColor: 'instrument.hairline',
          borderRadius: 1,
          backgroundColor: 'instrument.chrome',
          overflow: 'hidden',
        }}
      >
        {/* Column headers */}
        <Box
          sx={{
            display: 'grid',
            gridTemplateColumns: '120px 1fr 1px 1fr',
            alignItems: 'center',
            px: 1.5,
            py: 0.75,
            borderBottom: 1,
            borderColor: 'instrument.hairline',
          }}
        >
          <Box />
          <SubEyebrow>Our view</SubEyebrow>
          <ColumnDivider />
          <SubEyebrow>Fly</SubEyebrow>
        </Box>

        {flyPresent ? (
          <>
            <CompareRow
              label="State"
              ours={ourView.state}
              fly={flyView.state}
              drift={stateDrift}
            />
            <CompareRow
              label="Region"
              ours={ourView.region}
              fly={flyView.region}
              drift={regionDrift}
            />
            <CompareRow
              label="Last update"
              ours={formatIso(ourView.stateChangedAt)}
              fly={formatIso(flyView.createdAt)}
              mono
            />
            <CompareRow
              label="Machine ID"
              ours={ourView.flyMachineId ?? DASH}
              fly={flyView.id}
              drift={
                !!ourView.flyMachineId &&
                ourView.flyMachineId.toLowerCase() !== flyView.id.toLowerCase()
              }
              mono
              last
            />
          </>
        ) : (
          <>
            <CompareRow
              label="State"
              ours={ourView.state}
              fly={null}
              drift
              flyMissing
            />
            <CompareRow
              label="Region"
              ours={ourView.region}
              fly={null}
              flyMissing
            />
            <CompareRow
              label="Last update"
              ours={formatIso(ourView.stateChangedAt)}
              fly={null}
              flyMissing
              mono
            />
            <CompareRow
              label="Machine ID"
              ours={ourView.flyMachineId ?? DASH}
              fly={null}
              drift={machineIdMissing || !!ourView.flyMachineId}
              flyMissing
              mono
              last
            />
            <Box
              sx={{
                px: 1.5,
                py: 1.25,
                borderTop: 1,
                borderColor: 'instrument.hairline',
                backgroundColor: AMBER_TINT_BG,
              }}
            >
              <Stack direction="row" spacing={1} alignItems="center">
                <Box
                  sx={{
                    width: 8,
                    height: 8,
                    borderRadius: '50%',
                    bgcolor: AMBER_DOT,
                    flexShrink: 0,
                  }}
                />
                <Typography
                  variant="body2"
                  sx={{ fontSize: '0.8125rem', color: workspaceText.primary, fontWeight: 500 }}
                >
                  Fly machine not found
                </Typography>
              </Stack>
              <Typography
                variant="caption"
                sx={{
                  display: 'block',
                  mt: 0.25,
                  ml: 2.5,
                  fontSize: '0.75rem',
                  color: workspaceText.muted,
                }}
              >
                {ourView.flyMachineId ? (
                  <>
                    (machine ID <IdChip>{ourView.flyMachineId}</IdChip> no longer exists on Fly)
                  </>
                ) : (
                  '(never provisioned)'
                )}
              </Typography>
            </Box>
          </>
        )}
      </Box>
    </Box>
  )
}

interface CompareRowProps {
  label: string
  ours: string | null
  fly: string | null
  drift?: boolean
  flyMissing?: boolean
  mono?: boolean
  last?: boolean
}

function CompareRow({ label, ours, fly, drift, flyMissing, mono, last }: CompareRowProps) {
  return (
    <Box
      sx={{
        display: 'grid',
        gridTemplateColumns: '120px 1fr 1px 1fr',
        alignItems: 'center',
        px: 1.5,
        py: 0.875,
        borderBottom: last ? 0 : 1,
        borderColor: 'instrument.hairline',
        backgroundColor: drift ? AMBER_TINT_BG : 'transparent',
        position: 'relative',
      }}
    >
      {drift && (
        <Box
          sx={{
            position: 'absolute',
            left: 4,
            top: '50%',
            transform: 'translateY(-50%)',
            width: 6,
            height: 6,
            borderRadius: '50%',
            bgcolor: AMBER_DOT,
          }}
          aria-label="Drift"
        />
      )}
      <Typography
        sx={{
          fontSize: '0.75rem',
          textTransform: 'uppercase',
          letterSpacing: '0.04em',
          color: workspaceText.faint,
          fontWeight: 500,
        }}
      >
        {label}
      </Typography>
      <CellValue value={ours} mono={mono} />
      <ColumnDivider />
      {flyMissing ? (
        <Typography sx={{ fontSize: '0.8125rem', color: workspaceText.faint, fontStyle: 'italic' }}>
          —
        </Typography>
      ) : (
        <CellValue value={fly} mono={mono} />
      )}
    </Box>
  )
}

function CellValue({ value, mono }: { value: string | null; mono?: boolean }) {
  return (
    <Typography
      sx={{
        fontSize: '0.8125rem',
        color: workspaceText.primary,
        ...(mono ? monoNumberSx : {}),
        wordBreak: 'break-all',
        pr: 1,
      }}
    >
      {value ?? DASH}
    </Typography>
  )
}

function ColumnDivider() {
  return <Box sx={{ width: '1px', alignSelf: 'stretch', bgcolor: 'instrument.hairline' }} />
}

// ── Operations section ─────────────────────────────────────────────────────

interface OperationsSectionProps {
  operations: FlyOperationView[]
}

function OperationsSection({ operations }: OperationsSectionProps) {
  return (
    <Box>
      <SectionHeader
        icon={HistoryOutlinedIcon}
        label="Recent operations"
        meta={`${operations.length} op${operations.length === 1 ? '' : 's'}`}
      />

      <Box
        sx={{
          mt: 1,
          border: 1,
          borderColor: 'instrument.hairline',
          borderRadius: 1,
          backgroundColor: 'instrument.chrome',
          overflow: 'hidden',
        }}
      >
        {operations.length === 0 ? (
          <Box sx={{ px: 1.5, py: 2 }}>
            <Typography
              variant="body2"
              sx={{ fontSize: '0.8125rem', color: workspaceText.muted }}
            >
              No operations recorded yet.
            </Typography>
          </Box>
        ) : (
          operations.map((op, idx) => (
            <OperationRow key={op.id} op={op} last={idx === operations.length - 1} />
          ))
        )}
      </Box>
    </Box>
  )
}

interface OperationRowProps {
  op: FlyOperationView
  last: boolean
}

function OperationRow({ op, last }: OperationRowProps) {
  const [expanded, setExpanded] = useState(false)
  const toggle = useCallback(() => setExpanded((prev) => !prev), [])

  return (
    <Box
      sx={{
        borderBottom: last && !expanded ? 0 : 1,
        borderColor: 'instrument.hairline',
      }}
    >
      <Box
        component="button"
        type="button"
        onClick={toggle}
        aria-expanded={expanded}
        sx={{
          width: '100%',
          display: 'grid',
          gridTemplateColumns: '90px 1fr 110px 80px',
          alignItems: 'center',
          gap: 1,
          px: 1.5,
          py: 0.75,
          border: 0,
          outline: 0,
          cursor: 'pointer',
          textAlign: 'left',
          backgroundColor: expanded ? 'instrument.chipBg' : 'transparent',
          transition: 'background-color 120ms ease',
          '&:hover': { backgroundColor: 'instrument.chipHoverBg' },
          '&:focus-visible': {
            outline: `2px solid ${workspaceAccent.ink}`,
            outlineOffset: -2,
          },
        }}
      >
        <Typography
          sx={{
            ...monoNumberSx,
            fontSize: '0.75rem',
            color: workspaceText.muted,
          }}
        >
          <TimeAgo iso={op.createdAt} />
        </Typography>
        <Typography
          sx={{
            fontSize: '0.8125rem',
            color: workspaceText.primary,
            fontFamily: workspaceFontFamily.mono,
            overflow: 'hidden',
            textOverflow: 'ellipsis',
            whiteSpace: 'nowrap',
          }}
        >
          {op.operation}
        </Typography>
        <StatusChip status={op.status} />
        <Typography
          sx={{
            ...monoNumberSx,
            fontSize: '0.75rem',
            color: workspaceText.faint,
            textAlign: 'right',
          }}
        >
          {op.latencyMs != null ? `${op.latencyMs}ms` : DASH}
        </Typography>
      </Box>

      {expanded && (
        <Box
          sx={{
            px: 1.5,
            pb: 1.25,
            pt: 0.5,
            backgroundColor: 'instrument.chipBg',
            borderTop: 1,
            borderColor: 'instrument.hairline',
          }}
        >
          {op.errorCode && (
            <Typography
              sx={{
                fontSize: '0.75rem',
                color: semanticTokens.error,
                fontFamily: workspaceFontFamily.mono,
                mb: 0.75,
              }}
            >
              {op.errorCode}
              {op.httpStatusCode != null ? ` (HTTP ${op.httpStatusCode})` : ''}
            </Typography>
          )}
          <JsonBlock label="Request" payload={op.requestPayload} />
          <Box sx={{ height: 8 }} />
          <JsonBlock label="Response" payload={op.responsePayload ?? null} />
        </Box>
      )}
    </Box>
  )
}

function StatusChip({ status }: { status: string }) {
  const color = operationStatusColor(status)
  // A still-pending operation breathes so an in-flight machine command reads as
  // in-motion at a glance, matching the StatusDot pulse used on the other tabs.
  const pulse = status === 'Pending'
  return (
    <Box sx={{ display: 'inline-flex', alignItems: 'center', gap: 0.625 }}>
      <Box
        sx={{
          width: 7,
          height: 7,
          borderRadius: '50%',
          bgcolor: color,
          flexShrink: 0,
          animation: pulse ? `${pulseKeyframes} 2s ease-in-out infinite` : 'none',
          '@media (prefers-reduced-motion: reduce)': {
            animation: 'none',
            opacity: 1,
          },
        }}
      />
      <Typography sx={{ fontSize: '0.75rem', color, fontWeight: 500 }}>
        {status}
      </Typography>
    </Box>
  )
}

function JsonBlock({ label, payload }: { label: string; payload: string | null }) {
  const pretty = useMemo(() => prettyJson(payload), [payload])
  return (
    <Box>
      <Typography
        sx={{
          fontSize: '0.6875rem',
          textTransform: 'uppercase',
          letterSpacing: '0.04em',
          color: workspaceText.faint,
          fontWeight: 500,
          mb: 0.5,
        }}
      >
        {label}
      </Typography>
      <Box
        component="pre"
        sx={{
          m: 0,
          px: 1,
          py: 0.75,
          backgroundColor: 'instrument.codeBg',
          border: 1,
          borderColor: 'instrument.hairline',
          borderRadius: 0.5,
          fontFamily: workspaceFontFamily.mono,
          fontSize: '0.6875rem',
          lineHeight: 1.5,
          color: workspaceText.primary,
          overflowX: 'auto',
          whiteSpace: 'pre-wrap',
          wordBreak: 'break-all',
          maxHeight: 240,
          overflowY: 'auto',
        }}
      >
        {pretty}
      </Box>
    </Box>
  )
}

function prettyJson(raw: string | null): string {
  if (raw == null || raw === '') return '(empty)'
  try {
    const parsed = JSON.parse(raw)
    return JSON.stringify(parsed, null, 2)
  } catch {
    return raw
  }
}

// ── Shared atoms ───────────────────────────────────────────────────────────

function Eyebrow({ children }: { children: React.ReactNode }) {
  return (
    <Typography
      sx={{
        fontSize: '0.6875rem',
        textTransform: 'uppercase',
        letterSpacing: '0.06em',
        color: workspaceText.muted,
        fontWeight: 600,
      }}
    >
      {children}
    </Typography>
  )
}

/**
 * Section header — a tone-neutral IconTile + uppercase eyebrow label, with an
 * optional muted meta suffix (e.g. the operations count). Mirrors the
 * card/section header treatment used on the Sysstats and Timeline tabs.
 */
function SectionHeader({
  icon,
  label,
  meta,
}: {
  icon: SvgIconComponent
  label: string
  meta?: string
}) {
  return (
    <Stack direction="row" alignItems="center" spacing={0.75}>
      <IconTile icon={icon} tone="mute" size={22} />
      <Eyebrow>{label}</Eyebrow>
      {meta && (
        <Typography
          variant="caption"
          sx={{ fontSize: '0.75rem', color: workspaceText.faint, letterSpacing: '-0.005em' }}
        >
          · {meta}
        </Typography>
      )}
    </Stack>
  )
}

function SubEyebrow({ children }: { children: React.ReactNode }) {
  return (
    <Typography
      sx={{
        fontSize: '0.6875rem',
        textTransform: 'uppercase',
        letterSpacing: '0.04em',
        color: workspaceText.faint,
        fontWeight: 500,
      }}
    >
      {children}
    </Typography>
  )
}

function CenteredHint({ label, muted }: { label: string; muted?: boolean }) {
  return (
    <Stack
      direction="row"
      spacing={1.25}
      alignItems="center"
      sx={{ py: 4, justifyContent: 'center' }}
    >
      {!muted && <CircularProgress size={14} sx={{ color: workspaceText.muted }} />}
      <Typography variant="body2" sx={{ color: workspaceText.muted, fontSize: '0.8125rem' }}>
        {label}
      </Typography>
    </Stack>
  )
}

// ── Time / format helpers ──────────────────────────────────────────────────

interface TimeAgoProps {
  iso: string | null | undefined
}

/**
 * Light "Ns ago" formatter. Updates only on parent re-render — we lean on
 * the 15s refetch + the {@code dataUpdatedAt} change to keep this honest.
 * Pulling in a per-second ticker would be more churn than the value adds.
 */
function TimeAgo({ iso }: TimeAgoProps) {
  if (!iso) return <>{DASH}</>
  const then = Date.parse(iso)
  if (Number.isNaN(then)) return <>{DASH}</>
  const seconds = Math.max(0, Math.floor((Date.now() - then) / 1000))
  return <>{formatAge(seconds)} ago</>
}

function formatAge(seconds: number): string {
  if (seconds < 60) return `${seconds}s`
  const minutes = Math.floor(seconds / 60)
  if (minutes < 60) return `${minutes}m`
  const hours = Math.floor(minutes / 60)
  if (hours < 24) return `${hours}h`
  const days = Math.floor(hours / 24)
  return `${days}d`
}

function formatIso(iso: string | null | undefined): string {
  if (!iso) return DASH
  const t = Date.parse(iso)
  if (Number.isNaN(t)) return iso
  const d = new Date(t)
  return d.toISOString().replace('T', ' ').slice(0, 19) + 'Z'
}

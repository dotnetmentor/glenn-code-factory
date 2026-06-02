import { useMemo, useState } from 'react'
import { useLocation, useParams } from 'react-router-dom'
import {
  Alert,
  Box,
  Button,
  Chip,
  CircularProgress,
  Container,
  Divider,
  Paper,
  Stack,
  Typography,
} from '@mui/material'
import MemoryIcon from '@mui/icons-material/Memory'
import {
  RuntimeProposalStatus,
  type ProjectRuntimeSpecDto,
  type RuntimeProposalDto,
  useGetApiProjectsProjectId,
  useGetApiProjectsProjectIdProposals,
  useGetApiProjectsProjectIdRuntimeSpec,
} from '@/api/queries-commands'
import { useAgentHub } from '@/lib/signalr'
import { useBranchRuntimeStatus } from '@/applications/shared/runtime/hooks/useBranchRuntimeStatus'
import { useProposalSignalR } from '../hooks/useProposalSignalR'
import { RuntimeProposalCard } from '../components/RuntimeProposalCard'
import { RuntimeDrawer } from '../components/RuntimeDrawer'

interface LocationState {
  initialPrompt?: string
}

function shortId(id: string): string {
  return id.length > 8 ? id.slice(0, 8) : id
}

/**
 * Project runtime workspace surface (Spec 16, Scene 4).
 *
 * <p>Three vertical sections:
 * <ul>
 *   <li>Runtime status header — shows the current
 *       {@link ProjectRuntimeSpecDto} as language / service chips with a
 *       state badge.</li>
 *   <li>Pending proposals — any open {@link RuntimeProposalDto} rendered as
 *       full {@link RuntimeProposalCard}s with Approve / Edit / Reject
 *       affordances.</li>
 *   <li>Proposal history — a chronological list of past decisions rendered
 *       as collapsed cards.</li>
 * </ul>
 * </p>
 *
 * <p>SignalR live updates are handled by {@link useProposalSignalR}. The hub
 * connection is owned by this page (no app-wide provider yet) so it tears
 * down on navigation.</p>
 *
 * <p>Card 7 forwards the AI onboarding prompt via <c>location.state</c>. We
 * surface it as a banner here as a placeholder — actually kicking off an
 * agent session requires the agent-session-with-prompt machinery which
 * isn't wired in this card.</p>
 */
export function RuntimeWorkspacePage() {
  const { projectId = '' } = useParams<{ projectId: string }>()
  const location = useLocation()
  const initialPrompt = (location.state as LocationState | null)?.initialPrompt
  const [drawerOpen, setDrawerOpen] = useState(false)

  const { connection } = useAgentHub({
    projectId: projectId || undefined,
    enabled: !!projectId,
  })
  useProposalSignalR(connection, projectId)

  // Project query gives us defaultBranchId so the runtime drawer can call the
  // branch-scoped /runtime/status endpoint (the drawer header relies on it
  // for state, heartbeat freshness, and respawn counts).
  const projectQuery = useGetApiProjectsProjectId(projectId, {
    query: { enabled: !!projectId },
  })

  const specQuery = useGetApiProjectsProjectIdRuntimeSpec(projectId, {
    query: { enabled: !!projectId },
  })

  const defaultBranchId = projectQuery.data?.defaultBranchId ?? ''
  const { runtimeId } = useBranchRuntimeStatus(projectId, defaultBranchId, {
    enabled: !!projectId && !!defaultBranchId,
  })

  const pendingQuery = useGetApiProjectsProjectIdProposals(
    projectId,
    { status: RuntimeProposalStatus.Pending },
    { query: { enabled: !!projectId } },
  )

  const historyQuery = useGetApiProjectsProjectIdProposals(
    projectId,
    { limit: 50 },
    { query: { enabled: !!projectId } },
  )

  const { pending, history } = useMemo(() => {
    const pendingList: RuntimeProposalDto[] = pendingQuery.data ?? []
    const all: RuntimeProposalDto[] = historyQuery.data ?? []
    // History excludes Pending — those have their own section above.
    const historyList = all.filter(
      (p) => p.status !== RuntimeProposalStatus.Pending,
    )
    return { pending: pendingList, history: historyList }
  }, [pendingQuery.data, historyQuery.data])

  if (!projectId) {
    return (
      <Container maxWidth="md" sx={{ py: 4 }}>
        <Alert severity="error">No project id in URL.</Alert>
      </Container>
    )
  }

  return (
    <Container maxWidth="md" sx={{ py: 4 }}>
      <Stack spacing={3}>
        <Stack
          direction="row"
          spacing={2}
          alignItems="flex-end"
          justifyContent="space-between"
        >
          <Box>
            <Typography variant="overline" color="text.secondary">
              Project {shortId(projectId)}
            </Typography>
            <Typography variant="h4" component="h1">
              Runtime
            </Typography>
          </Box>
          <Button
            variant="outlined"
            startIcon={<MemoryIcon />}
            onClick={() => setDrawerOpen(true)}
            disabled={!projectId}
          >
            Runtime
          </Button>
        </Stack>

        {initialPrompt && (
          <Alert severity="info">
            <Stack spacing={0.5}>
              <Typography variant="subtitle2">Starting agent with</Typography>
              <Typography variant="body2" sx={{ whiteSpace: 'pre-wrap' }}>
                {initialPrompt}
              </Typography>
              <Typography variant="caption" color="text.secondary">
                The agent will pick this up once the runtime is online.
              </Typography>
            </Stack>
          </Alert>
        )}

        <RuntimeStatusHeader
          loading={specQuery.isPending}
          error={specQuery.error}
          spec={specQuery.data}
          pendingProposals={pending}
        />

        <Divider />

        <Box>
          <Typography variant="h6" sx={{ mb: 1.5 }}>
            Pending proposals
          </Typography>
          {pendingQuery.isPending ? (
            <Stack direction="row" spacing={1} alignItems="center">
              <CircularProgress size={14} />
              <Typography variant="body2" color="text.secondary">
                Loading…
              </Typography>
            </Stack>
          ) : pending.length === 0 ? (
            <Typography variant="body2" color="text.secondary">
              No pending proposals.
            </Typography>
          ) : (
            <Stack spacing={2}>
              {pending.map((p) => (
                <RuntimeProposalCard
                  key={p.id}
                  proposal={p}
                  projectId={projectId}
                />
              ))}
            </Stack>
          )}
        </Box>

        <Divider />

        <Box>
          <Typography variant="h6" sx={{ mb: 1.5 }}>
            Proposal history
          </Typography>
          {historyQuery.isPending ? (
            <Stack direction="row" spacing={1} alignItems="center">
              <CircularProgress size={14} />
              <Typography variant="body2" color="text.secondary">
                Loading…
              </Typography>
            </Stack>
          ) : history.length === 0 ? (
            <Typography variant="body2" color="text.secondary">
              No past proposals yet.
            </Typography>
          ) : (
            <Stack spacing={1}>
              {history.map((p) => (
                <RuntimeProposalCard
                  key={p.id}
                  proposal={p}
                  projectId={projectId}
                />
              ))}
            </Stack>
          )}
        </Box>
      </Stack>

      <RuntimeDrawer
        open={drawerOpen}
        onClose={() => setDrawerOpen(false)}
        projectId={projectId}
        branchId={defaultBranchId}
        runtimeId={runtimeId}
        connection={connection}
      />
    </Container>
  )
}

interface RuntimeStatusHeaderProps {
  loading: boolean
  error: unknown
  spec: ProjectRuntimeSpecDto | undefined
  pendingProposals: RuntimeProposalDto[]
}

/**
 * Minimal placeholder renderer for the V2 runtime spec. The V1 catalog
 * fields (languages / services / extras) are gone; the new
 * {@link ProjectRuntimeSpecDto} carries a freeform {@link RuntimeSpecV2}
 * (`install`, `services[]`, `setup`). A full UI surface ships in Phase 4 —
 * this just keeps the page rendering by showing the state badge plus a
 * service count and any install / setup snippets.
 */
function RuntimeStatusHeader({
  loading,
  error,
  spec,
  pendingProposals,
}: RuntimeStatusHeaderProps) {
  if (loading) {
    return (
      <Paper variant="outlined" sx={{ p: 2 }}>
        <Stack direction="row" spacing={1} alignItems="center">
          <CircularProgress size={14} />
          <Typography variant="body2" color="text.secondary">
            Loading runtime…
          </Typography>
        </Stack>
      </Paper>
    )
  }

  if (error) {
    return (
      <Alert severity="error">
        Failed to load runtime spec. Try refreshing the page.
      </Alert>
    )
  }

  if (!spec) {
    return (
      <Paper variant="outlined" sx={{ p: 2 }}>
        <Typography variant="body2" color="text.secondary">
          No stack yet — the agent will propose one shortly.
        </Typography>
      </Paper>
    )
  }

  const v2 = spec.spec
  const services = v2?.services ?? []
  // {@code spec.state} is the most-recent live runtime's state — null at the
  // wire when no runtimes exist (Tapper widens this to the enum type, but the
  // runtime can be null). Fall back to a placeholder so the chip stays
  // readable in the no-runtime case.
  const stateLabel = spec.state ? String(spec.state) : 'No runtime'
  const empty =
    !v2?.install && services.length === 0 && !v2?.setup

  return (
    <Paper variant="outlined" sx={{ p: 2 }}>
      <Stack spacing={1.5}>
        <Stack direction="row" spacing={1.5} alignItems="center">
          <Typography variant="subtitle2">Runtime</Typography>
          <Chip
            size="small"
            label={stateLabel}
            color={
              stateLabel === 'Online'
                ? 'success'
                : stateLabel === 'Failed' || stateLabel === 'Crashed'
                ? 'error'
                : 'default'
            }
          />
          {spec.runtimeId && (
            <Typography variant="caption" color="text.secondary">
              {shortId(spec.runtimeId)}
            </Typography>
          )}
        </Stack>
        {empty ? (
          <Typography variant="body2" color="text.secondary">
            {pendingProposals.length > 0
              ? 'Awaiting your decision on a stack proposal below.'
              : 'No stack yet — the agent will propose one shortly.'}
          </Typography>
        ) : (
          <Stack spacing={1}>
            {services.length > 0 && (
              <Stack direction="row" spacing={1} alignItems="center" flexWrap="wrap">
                <Typography variant="caption" color="text.secondary" sx={{ minWidth: 72 }}>
                  Services
                </Typography>
                {services.map((svc) => (
                  <Chip
                    key={svc.name}
                    size="small"
                    label={svc.name}
                    color="success"
                    variant="outlined"
                  />
                ))}
              </Stack>
            )}
            {v2?.install && (
              <Typography variant="caption" color="text.secondary">
                Install: {v2.install.length} char snippet
              </Typography>
            )}
            {v2?.setup && (
              <Typography variant="caption" color="text.secondary">
                Setup: {v2.setup.length} char snippet
              </Typography>
            )}
          </Stack>
        )}
      </Stack>
    </Paper>
  )
}

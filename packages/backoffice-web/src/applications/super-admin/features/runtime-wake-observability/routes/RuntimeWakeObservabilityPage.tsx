// v2 plan: this page currently feeds off read-time aggregation over the
// RuntimeEvents ring-buffer. Once volume / cost makes that untenable we swap
// the backend behind these endpoints for a RuntimeMetricsRollup table (hourly
// buckets keyed by stage + region, holding count / p50 / p95). The frontend
// stays exactly as-is — this page is the consumer, the read model behind it
// is the only thing that changes in v2.
import { useMemo, useState } from 'react'
import {
  Alert,
  Box,
  Divider,
  Stack,
  Typography,
} from '@mui/material'
import type { SlowWakeSession } from '@/api/queries-commands'
import { RuntimeDrawer } from '@/applications/super-admin/features/project-runtime'
import { useAgentHub } from '@/lib/signalr'
import { SummaryHeader } from '../components/SummaryHeader'
import { FilterRow } from '../components/FilterRow'
import { StageBreakdownChart } from '../components/StageBreakdownChart'
import { SlowSessionsTable } from '../components/SlowSessionsTable'
import { EmptyState } from '../components/EmptyState'
import { useWakeObservability } from '../hooks/useWakeObservability'

/**
 * Super-admin "Runtime Wake Observability" surface. Triage page for fleet
 * wake performance — p50/p95 summary, per-stage breakdown, and a slow-
 * sessions list that deep-links into the existing {@link RuntimeDrawer}.
 *
 * <p>Role-gated at the application level (see
 * {@code applications/super-admin/index.ts} — {@code requiresRole:
 * SuperAdmin}). Non-super-admins get the same access-denied treatment as
 * every other surface in this app.</p>
 */
export function RuntimeWakeObservabilityPage() {
  const {
    timeWindow,
    setTimeWindow,
    region,
    setRegion,
    summaryQuery,
    breakdownQuery,
    slowSessionsQuery,
    refresh,
  } = useWakeObservability()

  // Sessions that the user has clicked into. The drawer is mounted at the
  // page level (matching how RuntimeWorkspacePage wires it) so the same
  // public API — open / runtimeId / projectId / branchId / onClose — is
  // reused unchanged.
  const [selectedSession, setSelectedSession] = useState<SlowWakeSession | null>(
    null,
  )

  // The drawer's event-stream tab subscribes to the AgentHub. Branch-scoped
  // connections rekey on (projectId, branchId), so we open the hub only when
  // a session has been selected. While no row is open the hook short-circuits
  // and no socket is opened — same lazy pattern RuntimeWorkspacePage uses.
  const { connection } = useAgentHub({
    projectId: selectedSession?.projectId,
    branchId: selectedSession?.branchId,
    enabled: !!selectedSession,
  })

  const summary = summaryQuery.data
  const isAnyLoading =
    summaryQuery.isLoading ||
    breakdownQuery.isLoading ||
    slowSessionsQuery.isLoading
  const isAnyFetching =
    summaryQuery.isFetching ||
    breakdownQuery.isFetching ||
    slowSessionsQuery.isFetching

  // Region option list is derived from the slow-sessions response (kept
  // simple per the card — a dedicated region catalog endpoint can come
  // later if needed). De-duped + sorted so the picker is stable across
  // refetches.
  const regionOptions = useMemo(() => {
    const set = new Set<string>()
    for (const s of slowSessionsQuery.data?.sessions ?? []) {
      if (s.region) set.add(s.region)
    }
    return Array.from(set).sort()
  }, [slowSessionsQuery.data])

  const showEmptyState = !!summary && summary.count === 0

  return (
    <>
      <Stack spacing={4}>
            <Box>
              <Typography variant="overline" color="text.secondary">
                Super admin
              </Typography>
              <Typography variant="h4" component="h1" sx={{ mb: 0.5 }}>
                Runtime Wake Observability
              </Typography>
              <Typography variant="body2" color="text.secondary">
                Fleet wake performance — p50/p95, per-stage breakdown, and recent
                slow sessions for triage.
              </Typography>
            </Box>

            {summaryQuery.error instanceof Error && (
              <Alert severity="error">
                Failed to load wake summary: {summaryQuery.error.message}
              </Alert>
            )}

            <SummaryHeader
              summary={summary}
              isLoading={summaryQuery.isLoading}
              isFetching={isAnyFetching}
              onRefresh={refresh}
            />

            <FilterRow
              timeWindow={timeWindow}
              onTimeWindowChange={setTimeWindow}
              region={region}
              onRegionChange={setRegion}
              regionOptions={regionOptions}
            />

            <Divider />

            {showEmptyState ? (
              <EmptyState
                title="No completed wakes in this window"
                description="Try widening the time window or removing the region filter."
              />
            ) : (
              <Stack spacing={3}>
                <StageBreakdownChart
                  breakdown={breakdownQuery.data}
                  isLoading={breakdownQuery.isLoading || isAnyLoading}
                  error={breakdownQuery.error}
                />
                <SlowSessionsTable
                  sessions={slowSessionsQuery.data?.sessions}
                  isLoading={slowSessionsQuery.isLoading}
                  error={slowSessionsQuery.error}
                  onSelect={setSelectedSession}
                />
              </Stack>
            )}
          </Stack>

      <RuntimeDrawer
        open={!!selectedSession}
        onClose={() => setSelectedSession(null)}
        projectId={selectedSession?.projectId ?? ''}
        branchId={selectedSession?.branchId}
        runtimeId={selectedSession?.runtimeId}
        connection={connection}
      />
    </>
  )
}

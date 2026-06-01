import { useCallback, useMemo } from 'react'
import { useSearchParams } from 'react-router-dom'
import { useQueryClient } from '@tanstack/react-query'
import {
  getGetApiAdminRuntimeWakeObservabilitySlowSessionsQueryKey,
  getGetApiAdminRuntimeWakeObservabilityStageBreakdownQueryKey,
  getGetApiAdminRuntimeWakeObservabilitySummaryQueryKey,
  useGetApiAdminRuntimeWakeObservabilitySlowSessions,
  useGetApiAdminRuntimeWakeObservabilityStageBreakdown,
  useGetApiAdminRuntimeWakeObservabilitySummary,
} from '@/api/queries-commands'

/**
 * Time windows the page exposes. The backend treats {@code window} as an
 * opaque string, so the typed union here is purely a client-side convenience
 * for the picker — any value the user sets in the URL still passes through
 * to the API unchanged.
 */
export const WAKE_TIME_WINDOWS = ['1h', '24h', '7d'] as const
export type WakeTimeWindow = (typeof WAKE_TIME_WINDOWS)[number]

const DEFAULT_WINDOW: WakeTimeWindow = '24h'
const SLOW_SESSIONS_LIMIT = 20

function isWakeTimeWindow(value: string | null): value is WakeTimeWindow {
  return (
    value !== null &&
    (WAKE_TIME_WINDOWS as readonly string[]).includes(value)
  )
}

/**
 * URL-driven filter state + the three Orval queries that feed the page. The
 * page is shareable / linkable from chat (e.g. `?window=7d&region=ord`), so
 * both filters round-trip through the search string rather than living in
 * component state.
 */
export function useWakeObservability() {
  const [searchParams, setSearchParams] = useSearchParams()
  const queryClient = useQueryClient()

  const rawWindow = searchParams.get('window')
  const timeWindow: WakeTimeWindow = isWakeTimeWindow(rawWindow)
    ? rawWindow
    : DEFAULT_WINDOW
  const region = searchParams.get('region') ?? undefined

  // Build query params once — passed straight through to all three endpoints
  // so they aggregate over the same slice.
  const params = useMemo(
    () => ({
      window: timeWindow,
      ...(region ? { region } : {}),
    }),
    [timeWindow, region],
  )

  const summaryQuery = useGetApiAdminRuntimeWakeObservabilitySummary(params)
  const breakdownQuery =
    useGetApiAdminRuntimeWakeObservabilityStageBreakdown(params)
  const slowSessionsQuery = useGetApiAdminRuntimeWakeObservabilitySlowSessions(
    { ...params, limit: SLOW_SESSIONS_LIMIT },
  )

  const setTimeWindow = useCallback(
    (next: WakeTimeWindow) => {
      setSearchParams(
        (prev) => {
          const updated = new URLSearchParams(prev)
          if (next === DEFAULT_WINDOW) {
            updated.delete('window')
          } else {
            updated.set('window', next)
          }
          return updated
        },
        { replace: true },
      )
    },
    [setSearchParams],
  )

  const setRegion = useCallback(
    (next: string | undefined) => {
      setSearchParams(
        (prev) => {
          const updated = new URLSearchParams(prev)
          if (!next) {
            updated.delete('region')
          } else {
            updated.set('region', next)
          }
          return updated
        },
        { replace: true },
      )
    },
    [setSearchParams],
  )

  const refresh = useCallback(() => {
    // Invalidate by URL prefix so any cached variant of the three endpoints
    // refetches — the page-level URL params are stable across a single
    // refresh click but the prefix invalidation guards against drift.
    queryClient.invalidateQueries({
      queryKey: getGetApiAdminRuntimeWakeObservabilitySummaryQueryKey(params),
    })
    queryClient.invalidateQueries({
      queryKey:
        getGetApiAdminRuntimeWakeObservabilityStageBreakdownQueryKey(params),
    })
    queryClient.invalidateQueries({
      queryKey: getGetApiAdminRuntimeWakeObservabilitySlowSessionsQueryKey({
        ...params,
        limit: SLOW_SESSIONS_LIMIT,
      }),
    })
  }, [queryClient, params])

  return {
    timeWindow,
    setTimeWindow,
    region,
    setRegion,
    summaryQuery,
    breakdownQuery,
    slowSessionsQuery,
    refresh,
  }
}

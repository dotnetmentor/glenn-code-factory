import { useMemo } from 'react'
import { useGetApiAdminRuntimePresetsMiseVersions } from '@/api/queries-commands'

export interface UseMiseVersionsResult {
  versions: string[]
  isLoading: boolean
  isError: boolean
  tool: string
}

/**
 * Thin wrapper around the generated {@code useGetApiAdminRuntimePresetsMiseVersions}
 * Orval hook. Returns the version list plus loading / error flags so the
 * preset editor can render a popover of available versions without
 * threading three separate state values.
 *
 * <p>The query is disabled until {@code tool} is a non-empty string, so
 * the caller can pass an unfilled MiseTool field without firing a request.</p>
 */
export function useMiseVersions(tool: string | undefined): UseMiseVersionsResult {
  const enabled = !!tool && tool.trim().length > 0
  const query = useGetApiAdminRuntimePresetsMiseVersions(
    { tool: enabled ? tool : undefined },
    { query: { enabled, staleTime: 60_000 } },
  )

  return useMemo(
    () => ({
      versions: query.data?.versions ?? [],
      tool: query.data?.tool ?? tool ?? '',
      isLoading: query.isLoading,
      isError: query.isError,
    }),
    [query.data, query.isLoading, query.isError, tool],
  )
}

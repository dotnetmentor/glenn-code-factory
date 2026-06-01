import { useCallback, useState } from 'react'
import {
  getApiEnvironmentExport,
  usePostApiEnvironmentImport,
  type EnvironmentSnapshotDto,
  type EnvironmentImportSummary,
} from '../../../../../api/queries-commands'

function errorMessage(err: unknown): string {
  if (err instanceof Error) return err.message
  if (typeof err === 'string') return err
  return 'Unknown error'
}

/**
 * State + actions for the Environment Backup page.
 *
 * Export is a query, but we want it on-demand (not on mount), so we call the
 * generated query function `getApiEnvironmentExport` directly rather than the
 * `useGetApiEnvironmentExport` hook. Import is a mutation.
 */
export function useEnvironmentBackup() {
  // ---- Export ----
  const [exporting, setExporting] = useState(false)
  const [exportError, setExportError] = useState<string | null>(null)
  const [exportJson, setExportJson] = useState<string | null>(null)

  const runExport = useCallback(async (): Promise<string | null> => {
    setExporting(true)
    setExportError(null)
    try {
      const snapshot = await getApiEnvironmentExport()
      const json = JSON.stringify(snapshot, null, 2)
      setExportJson(json)
      return json
    } catch (err) {
      setExportError(errorMessage(err))
      return null
    } finally {
      setExporting(false)
    }
  }, [])

  // ---- Import ----
  const importMutation = usePostApiEnvironmentImport()
  const [importSummary, setImportSummary] = useState<EnvironmentImportSummary | null>(
    null,
  )
  const [importError, setImportError] = useState<string | null>(null)

  const runImport = useCallback(
    async (snapshot: EnvironmentSnapshotDto): Promise<EnvironmentImportSummary | null> => {
      setImportError(null)
      setImportSummary(null)
      try {
        const summary = await importMutation.mutateAsync({ data: snapshot })
        setImportSummary(summary)
        return summary
      } catch (err) {
        setImportError(errorMessage(err))
        return null
      }
    },
    [importMutation],
  )

  return {
    // export
    exporting,
    exportError,
    exportJson,
    runExport,
    // import
    importing: importMutation.isPending,
    importError,
    importSummary,
    runImport,
  }
}

/**
 * Parse + lightweight client-side validation of a pasted JSON blob.
 * Returns the parsed snapshot on success or an error string otherwise.
 */
export function parseSnapshot(
  raw: string,
): { snapshot: EnvironmentSnapshotDto; error: null } | { snapshot: null; error: string } {
  const trimmed = raw.trim()
  if (!trimmed) {
    return { snapshot: null, error: 'Paste the environment backup JSON first.' }
  }

  let parsed: unknown
  try {
    parsed = JSON.parse(trimmed)
  } catch {
    return { snapshot: null, error: 'That is not valid JSON. Check for a missing brace or trailing comma.' }
  }

  if (typeof parsed !== 'object' || parsed === null || Array.isArray(parsed)) {
    return { snapshot: null, error: 'The backup must be a JSON object.' }
  }

  const candidate = parsed as Partial<EnvironmentSnapshotDto>
  if (typeof candidate.version !== 'string' || candidate.version.length === 0) {
    return {
      snapshot: null,
      error: 'This does not look like an environment backup — the required "version" field is missing.',
    }
  }

  return { snapshot: candidate as EnvironmentSnapshotDto, error: null }
}

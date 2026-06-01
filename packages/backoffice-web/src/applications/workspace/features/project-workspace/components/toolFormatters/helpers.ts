/**
 * Defensive parsing + display helpers shared by every formatter.
 *
 * <p>The wire payload ships {@code args} / {@code result} as raw JSON strings
 * (Postgres jsonb columns flattened by Tapper). Each helper here narrows the
 * arbitrary {@code unknown} to a useful value without throwing — every
 * fallback returns {@code undefined} or a sensible default so the caller can
 * use plain {@code ?? 'fallback'} chains.</p>
 */

/**
 * Parse the raw {@code args} or {@code result} JSON string defensively.
 *
 * @returns the parsed value (always cast to {@code Record<string, unknown>}
 *   for ergonomic indexed access — callers narrow further with the typed
 *   helpers below) or {@code undefined} when the input is null / empty / not
 *   valid JSON / not an object.
 */
export function safeParseRecord(
  raw: string | null | undefined,
): Record<string, unknown> | undefined {
  if (!raw) return undefined
  try {
    const parsed = JSON.parse(raw)
    if (parsed && typeof parsed === 'object' && !Array.isArray(parsed)) {
      return parsed as Record<string, unknown>
    }
  } catch {
    // Swallow — the caller falls back to the generic label.
  }
  return undefined
}

/** Return {@code value} only if it's a non-empty string. */
export function asString(value: unknown): string | undefined {
  if (typeof value === 'string' && value.length > 0) return value
  return undefined
}

/** Return {@code value} only if it's a finite number. */
export function asNumber(value: unknown): number | undefined {
  if (typeof value === 'number' && Number.isFinite(value)) return value
  return undefined
}

/**
 * Trim a string to {@code max} characters, appending an ellipsis if cut.
 * Leaves shorter strings untouched.
 */
export function truncate(value: string, max: number): string {
  if (value.length <= max) return value
  return value.slice(0, Math.max(0, max - 1)) + '…'
}

/**
 * Return the last path segment for forward- or back-slashed paths. Falls back
 * to the full string when no separator is found, and to an empty string when
 * the input is empty/whitespace.
 */
export function basename(path: string): string {
  const trimmed = path.trim()
  if (!trimmed) return ''
  // Strip any trailing slashes so "/foo/bar/" yields "bar".
  const cleaned = trimmed.replace(/[/\\]+$/u, '')
  if (!cleaned) return trimmed
  const lastSlash = Math.max(cleaned.lastIndexOf('/'), cleaned.lastIndexOf('\\'))
  return lastSlash === -1 ? cleaned : cleaned.slice(lastSlash + 1)
}

/**
 * Format a millisecond duration as the kind of compact label the pill shows:
 * sub-second values render as ms ({@code "382ms"}), the rest as seconds with
 * one decimal ({@code "1.4s"}). Returns {@code undefined} for non-finite
 * inputs so callers can omit the segment entirely.
 */
export function formatDuration(ms: number | undefined): string | undefined {
  if (ms === undefined || !Number.isFinite(ms) || ms < 0) return undefined
  if (ms < 1000) return `${Math.round(ms)}ms`
  return `${(ms / 1000).toFixed(1)}s`
}

/**
 * Try several candidate keys on a parsed record, returning the first one that
 * resolves to a non-empty string. Used because Cursor / Claude / OpenCode
 * historically use slightly different field names for the same concept
 * ({@code path} vs {@code file_path} vs {@code filePath}).
 */
export function pickString(
  record: Record<string, unknown> | undefined,
  keys: readonly string[],
): string | undefined {
  if (!record) return undefined
  for (const key of keys) {
    const value = asString(record[key])
    if (value !== undefined) return value
  }
  return undefined
}

/**
 * Same as {@link pickString} but for numeric fields.
 */
export function pickNumber(
  record: Record<string, unknown> | undefined,
  keys: readonly string[],
): number | undefined {
  if (!record) return undefined
  for (const key of keys) {
    const value = asNumber(record[key])
    if (value !== undefined) return value
  }
  return undefined
}

/**
 * Extract a hostname from a URL string. Returns the original input on parse
 * failure, or {@code undefined} when the input is empty.
 */
export function safeHostname(url: string | undefined): string | undefined {
  if (!url) return undefined
  try {
    return new URL(url).hostname || url
  } catch {
    return url
  }
}

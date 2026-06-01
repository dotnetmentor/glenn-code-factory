import { Box, Typography } from '@mui/material'

/** Check if a value is a complex type (object or array) worth showing as a diff */
export function isComplexValue(value: unknown): boolean {
  return value !== null && typeof value === 'object'
}

/** Try to parse a string as JSON, return parsed value or null */
export function tryParseJson(value: string | null | undefined): unknown | null {
  if (!value) return null
  try {
    const parsed = JSON.parse(value)
    if (typeof parsed === 'object' && parsed !== null) return parsed
    return null
  } catch {
    return null
  }
}

/** Sort an object's keys for stable comparison */
function sortObjectKeys(obj: Record<string, unknown>): Record<string, unknown> {
  const sorted: Record<string, unknown> = {}
  for (const key of Object.keys(obj).sort()) {
    sorted[key] = obj[key]
  }
  return sorted
}

/** Normalize a JSON value for stable comparison — sorts object keys and arrays of objects */
function normalizeForDiff(value: unknown): unknown {
  if (value === null || value === undefined) return value
  if (Array.isArray(value)) {
    const normalized = value.map(item => normalizeForDiff(item))
    if (normalized.length > 0 && typeof normalized[0] === 'object' && normalized[0] !== null) {
      normalized.sort((a, b) => JSON.stringify(a).localeCompare(JSON.stringify(b)))
    }
    return normalized
  }
  if (typeof value === 'object') {
    const obj = value as Record<string, unknown>
    const sorted = sortObjectKeys(obj)
    const result: Record<string, unknown> = {}
    for (const [k, v] of Object.entries(sorted)) {
      result[k] = normalizeForDiff(v)
    }
    return result
  }
  return value
}

type DiffLine = { type: 'add' | 'remove' | 'context'; text: string }

/** Compute a git-style unified diff between two JSON values */
function computeJsonDiff(oldVal: unknown, newVal: unknown): DiffLine[] | null {
  const normOld = normalizeForDiff(oldVal)
  const normNew = normalizeForDiff(newVal)

  const oldStr = JSON.stringify(normOld, null, 2)
  const newStr = JSON.stringify(normNew, null, 2)

  if (oldStr === newStr) return null

  const oldLines = oldStr.split('\n')
  const newLines = newStr.split('\n')

  const lcs = computeLCS(oldLines, newLines)
  const result: DiffLine[] = []
  let oi = 0, ni = 0, li = 0

  while (oi < oldLines.length || ni < newLines.length) {
    if (li < lcs.length && oi < oldLines.length && ni < newLines.length && oldLines[oi] === lcs[li] && newLines[ni] === lcs[li]) {
      result.push({ type: 'context', text: oldLines[oi] })
      oi++; ni++; li++
    } else if (oi < oldLines.length && (li >= lcs.length || oldLines[oi] !== lcs[li])) {
      result.push({ type: 'remove', text: oldLines[oi] })
      oi++
    } else if (ni < newLines.length && (li >= lcs.length || newLines[ni] !== lcs[li])) {
      result.push({ type: 'add', text: newLines[ni] })
      ni++
    }
  }

  return result
}

/** Compute Longest Common Subsequence of two string arrays */
function computeLCS(a: string[], b: string[]): string[] {
  const m = a.length, n = b.length
  if (m * n > 50000) {
    return []
  }
  const dp: number[][] = Array.from({ length: m + 1 }, () => Array(n + 1).fill(0))
  for (let i = 1; i <= m; i++) {
    for (let j = 1; j <= n; j++) {
      dp[i][j] = a[i - 1] === b[j - 1] ? dp[i - 1][j - 1] + 1 : Math.max(dp[i - 1][j], dp[i][j - 1])
    }
  }
  const result: string[] = []
  let i = m, j = n
  while (i > 0 && j > 0) {
    if (a[i - 1] === b[j - 1]) {
      result.unshift(a[i - 1])
      i--; j--
    } else if (dp[i - 1][j] > dp[i][j - 1]) {
      i--
    } else {
      j--
    }
  }
  return result
}

type CollapsedLine = DiffLine | { type: 'separator' }

/** Collapse long runs of context lines, keeping `margin` lines around changes */
function collapseContext(lines: DiffLine[], margin: number): CollapsedLine[] {
  const nearChange = new Set<number>()
  for (let i = 0; i < lines.length; i++) {
    if (lines[i].type !== 'context') {
      for (let j = Math.max(0, i - margin); j <= Math.min(lines.length - 1, i + margin); j++) {
        nearChange.add(j)
      }
    }
  }

  if (nearChange.size === lines.length) return lines

  const result: CollapsedLine[] = []
  let inCollapse = false
  for (let i = 0; i < lines.length; i++) {
    if (nearChange.has(i) || lines[i].type !== 'context') {
      if (inCollapse) {
        result.push({ type: 'separator' })
        inCollapse = false
      }
      result.push(lines[i])
    } else {
      inCollapse = true
    }
  }
  return result
}

/** Renders a git-style unified diff between two JSON values */
export function JsonDiffView({ oldVal, newVal, propName }: { oldVal: unknown; newVal: unknown; propName: string }) {
  const diff = computeJsonDiff(oldVal, newVal)

  if (diff === null) {
    return (
      <Box sx={{ display: 'flex', alignItems: 'baseline', gap: 1 }}>
        <Typography variant="body2" sx={{ fontWeight: 600, minWidth: 160, fontFamily: 'monospace', fontSize: '0.8rem' }}>
          {propName}:
        </Typography>
        <Typography variant="body2" sx={{ color: 'text.secondary', fontFamily: 'monospace', fontSize: '0.8rem' }}>
          (reorder only, no changes)
        </Typography>
      </Box>
    )
  }

  const collapsedDiff = collapseContext(diff, 2)

  return (
    <Box sx={{ display: 'flex', flexDirection: 'column', gap: 0 }}>
      <Typography variant="body2" sx={{ fontWeight: 600, fontFamily: 'monospace', fontSize: '0.8rem', mb: 0.5 }}>
        {propName}:
      </Typography>
      <Box
        sx={{
          fontFamily: '"SF Mono", "Fira Code", "Consolas", monospace',
          fontSize: '0.75rem',
          lineHeight: 1.5,
          borderRadius: 1,
          overflow: 'auto',
          border: '1px solid',
          borderColor: 'divider',
          bgcolor: '#fafafa',
        }}
      >
        {collapsedDiff.map((line, i) => {
          if (line.type === 'separator') {
            return (
              <Box key={i} sx={{ px: 1.5, py: 0.25, bgcolor: '#f0f0f0', color: 'text.disabled', borderTop: '1px solid', borderBottom: '1px solid', borderColor: 'divider' }}>
                ···
              </Box>
            )
          }
          const bgColor = line.type === 'add' ? '#e6ffec' : line.type === 'remove' ? '#ffebe9' : 'transparent'
          const textColor = line.type === 'add' ? '#1a7f37' : line.type === 'remove' ? '#cf222e' : 'text.primary'
          const prefix = line.type === 'add' ? '+' : line.type === 'remove' ? '-' : ' '
          return (
            <Box key={i} sx={{ px: 1.5, py: 0, bgcolor: bgColor, color: textColor, whiteSpace: 'pre' }}>
              {prefix} {line.text}
            </Box>
          )
        })}
      </Box>
    </Box>
  )
}

/** Renders a single JSON value (for "Created" events where there's only a new value, or "Deleted" where there's only old) */
export function JsonValueView({ value, propName, variant }: { value: unknown; propName: string; variant: 'added' | 'removed' }) {
  const formatted = JSON.stringify(value, null, 2)
  const lines = formatted.split('\n')
  const bgColor = variant === 'added' ? '#e6ffec' : '#ffebe9'
  const textColor = variant === 'added' ? '#1a7f37' : '#cf222e'
  const prefix = variant === 'added' ? '+' : '-'

  return (
    <Box sx={{ display: 'flex', flexDirection: 'column', gap: 0 }}>
      <Typography variant="body2" sx={{ fontWeight: 600, fontFamily: 'monospace', fontSize: '0.8rem', mb: 0.5 }}>
        {propName}:
      </Typography>
      <Box
        sx={{
          fontFamily: '"SF Mono", "Fira Code", "Consolas", monospace',
          fontSize: '0.75rem',
          lineHeight: 1.5,
          borderRadius: 1,
          overflow: 'auto',
          border: '1px solid',
          borderColor: 'divider',
          bgcolor: '#fafafa',
        }}
      >
        {lines.map((line, i) => (
          <Box key={i} sx={{ px: 1.5, py: 0, bgcolor: bgColor, color: textColor, whiteSpace: 'pre' }}>
            {prefix} {line}
          </Box>
        ))}
      </Box>
    </Box>
  )
}

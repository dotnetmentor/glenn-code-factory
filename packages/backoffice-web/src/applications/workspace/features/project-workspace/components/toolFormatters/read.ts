/**
 * Formatter for file-read tools (Cursor SDK names: {@code read},
 * {@code Read}, {@code read_file}).
 *
 * <p>Active label shows the full path (so the user sees what they asked for);
 * summary collapses to basename + line count once the read returns.</p>
 */
import type { FormatterOutput, ToolFormatter } from './types'
import { basename, pickNumber, pickString, safeParseRecord } from './helpers'

export const readFormatter: ToolFormatter = (event): FormatterOutput => {
  const args = safeParseRecord(event.args)
  const result = safeParseRecord(event.result)

  const path =
    pickString(args, ['path', 'file_path', 'filePath', 'target_file']) ??
    '(unknown)'
  const base = basename(path) || path

  const lineCount = pickNumber(result, [
    'lineCount',
    'line_count',
    'lines',
    'numLines',
    'num_lines',
  ])
  const reason = pickString(result, ['error', 'reason', 'message'])

  const summary =
    lineCount !== undefined
      ? `Read ${lineCount} lines from \`${base}\``
      : `Read \`${base}\``

  return {
    activeLabel: `Reading \`${path}\``,
    summary,
    errorVariant: reason
      ? `Couldn't read \`${base}\` — ${reason}`
      : `Couldn't read \`${base}\``,
    glyph: 'Description',
  }
}

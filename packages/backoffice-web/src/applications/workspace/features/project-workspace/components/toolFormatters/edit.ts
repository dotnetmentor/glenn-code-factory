/**
 * Formatter for in-place edit tools (Cursor SDK names: {@code edit},
 * {@code Edit}, {@code MultiEdit}, {@code apply_edit}).
 *
 * <p>The summary surfaces a diff-style {@code (+added −removed)} segment if
 * the result carries those counts; otherwise it falls back to a plain "Edited
 * `<basename>`".</p>
 */
import type { FormatterOutput, ToolFormatter } from './types'
import { basename, pickNumber, pickString, safeParseRecord } from './helpers'

export const editFormatter: ToolFormatter = (event): FormatterOutput => {
  const args = safeParseRecord(event.args)
  const result = safeParseRecord(event.result)

  const path =
    pickString(args, ['path', 'file_path', 'filePath', 'target_file']) ??
    '(unknown)'
  const base = basename(path) || path

  const added = pickNumber(result, [
    'added',
    'linesAdded',
    'lines_added',
    'additions',
  ])
  const removed = pickNumber(result, [
    'removed',
    'linesRemoved',
    'lines_removed',
    'deletions',
  ])

  let summary: string
  if (added !== undefined || removed !== undefined) {
    // Use the en-dash + minus sign vocabulary from the spec.
    summary = `Edited \`${base}\` (+${added ?? 0} −${removed ?? 0})`
  } else {
    summary = `Edited \`${base}\``
  }

  return {
    activeLabel: `Editing \`${base}\``,
    summary,
    errorVariant: `Edit failed for \`${base}\``,
    glyph: 'Edit',
  }
}

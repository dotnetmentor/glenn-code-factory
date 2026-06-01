/**
 * Formatter for directory-listing tools (Cursor SDK names: {@code glob},
 * {@code Glob}, {@code ls}, {@code LS}, {@code list_files}).
 *
 * <p>Glob and {@code ls} share a formatter because the user-visible
 * vocabulary is the same — "listing entries in a path".</p>
 */
import type { FormatterOutput, ToolFormatter } from './types'
import { pickNumber, pickString, safeParseRecord } from './helpers'

export const globFormatter: ToolFormatter = (event): FormatterOutput => {
  const args = safeParseRecord(event.args)
  const result = safeParseRecord(event.result)

  const path =
    pickString(args, ['path', 'pattern', 'glob', 'directory', 'dir']) ?? '.'

  const entries = pickNumber(result, [
    'entries',
    'entryCount',
    'entry_count',
    'count',
    'matches',
    'files',
  ])

  const summary =
    entries !== undefined
      ? `Listed ${entries} entries in \`${path}\``
      : `Listed entries in \`${path}\``

  return {
    activeLabel: `Listing \`${path}\``,
    summary,
    errorVariant: `Path not found: \`${path}\``,
    glyph: 'FolderOpen',
  }
}

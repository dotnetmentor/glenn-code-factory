/**
 * Formatter for content-search tools (Cursor SDK names: {@code grep},
 * {@code Grep}, {@code search}).
 *
 * <p>{@code N=0} renders as a distinct "No matches" label so the user
 * doesn't have to read past a zero-count line to learn the search came up
 * empty.</p>
 */
import type { FormatterOutput, ToolFormatter } from './types'
import { pickNumber, pickString, safeParseRecord } from './helpers'

export const grepFormatter: ToolFormatter = (event): FormatterOutput => {
  const args = safeParseRecord(event.args)
  const result = safeParseRecord(event.result)

  const pattern =
    pickString(args, ['pattern', 'query', 'regex', 'search']) ?? '(no pattern)'

  const matches = pickNumber(result, [
    'matches',
    'matchCount',
    'match_count',
    'count',
    'total',
  ])
  const files = pickNumber(result, [
    'files',
    'fileCount',
    'file_count',
    'filesMatched',
    'files_matched',
  ])

  let summary: string
  if (matches === 0) {
    summary = `No matches for \`${pattern}\``
  } else if (matches !== undefined && files !== undefined) {
    summary = `Found ${matches} matches across ${files} files for \`${pattern}\``
  } else if (matches !== undefined) {
    summary = `Found ${matches} matches for \`${pattern}\``
  } else {
    summary = `Searched for \`${pattern}\``
  }

  return {
    activeLabel: `Searching for \`${pattern}\``,
    summary,
    errorVariant: 'Search failed',
    glyph: 'Search',
  }
}

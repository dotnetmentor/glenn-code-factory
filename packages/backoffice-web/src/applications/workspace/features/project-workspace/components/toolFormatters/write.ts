/**
 * Formatter for file-write tools (Cursor SDK names: {@code write},
 * {@code Write}, {@code write_file}).
 *
 * <p>Line count for the summary is best-effort: if {@code result} carries it
 * we use it; otherwise we count newlines in the args content, falling back to
 * the bare "Wrote `<basename>`" label.</p>
 */
import type { FormatterOutput, ToolFormatter } from './types'
import { basename, pickNumber, pickString, safeParseRecord } from './helpers'

export const writeFormatter: ToolFormatter = (event): FormatterOutput => {
  const args = safeParseRecord(event.args)
  const result = safeParseRecord(event.result)

  const path =
    pickString(args, ['path', 'file_path', 'filePath', 'target_file']) ??
    '(unknown)'
  const base = basename(path) || path

  // Prefer an explicit line count from the result, fall back to counting
  // newlines in the written content so we still show something useful.
  let lineCount = pickNumber(result, [
    'lineCount',
    'line_count',
    'lines',
    'numLines',
    'num_lines',
  ])
  if (lineCount === undefined) {
    const content = pickString(args, ['content', 'contents', 'text', 'body'])
    if (content !== undefined) {
      lineCount = content === '' ? 0 : content.split('\n').length
    }
  }

  const summary =
    lineCount !== undefined
      ? `Wrote \`${base}\` (${lineCount} lines)`
      : `Wrote \`${base}\``

  return {
    activeLabel: `Writing \`${base}\``,
    summary,
    errorVariant: `Write failed for \`${base}\``,
    glyph: 'NoteAdd',
  }
}

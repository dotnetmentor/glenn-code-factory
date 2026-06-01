/**
 * Formatter for HTTP fetch tools (Cursor SDK names: {@code webfetch},
 * {@code WebFetch}, {@code web_fetch}, {@code fetch}).
 *
 * <p>The hostname is the most useful headline — full URLs are too long for
 * the pill and the query string is rarely interesting. The body length goes
 * into the summary so the user can see at a glance whether the fetch
 * returned anything.</p>
 */
import type { FormatterOutput, ToolFormatter } from './types'
import {
  asString,
  pickNumber,
  pickString,
  safeHostname,
  safeParseRecord,
} from './helpers'

export const webfetchFormatter: ToolFormatter = (event): FormatterOutput => {
  const args = safeParseRecord(event.args)
  const result = safeParseRecord(event.result)

  const url = pickString(args, ['url', 'href', 'uri', 'link'])
  const host = safeHostname(url) ?? '(unknown host)'

  // Prefer an explicit content length, fall back to measuring the body.
  let chars = pickNumber(result, [
    'chars',
    'charCount',
    'char_count',
    'length',
    'contentLength',
    'content_length',
    'bytes',
  ])
  if (chars === undefined) {
    const body = asString(result?.['body']) ?? asString(result?.['content'])
    if (body !== undefined) chars = body.length
  }

  const summary =
    chars !== undefined
      ? `Fetched ${host} (${chars} chars)`
      : `Fetched ${host}`

  return {
    activeLabel: `Fetching ${host}`,
    summary,
    errorVariant: `Fetch failed: ${host}`,
    glyph: 'Public',
  }
}

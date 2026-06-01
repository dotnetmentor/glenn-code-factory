/**
 * Generic fallback formatter — used when {@code event.name} isn't registered.
 *
 * <p>The active label shows an 80-character preview of {@code args} so the
 * user still gets *some* signal about what an unknown tool is doing. The
 * preview prefers the raw JSON string (since we don't know the shape) but
 * strips outer braces/quotes to keep it readable.</p>
 */
import type { FormatterOutput, ToolFormatter } from './types'
import { truncate } from './helpers'

const ARGS_PREVIEW_MAX = 80

/** Strip the kind of noise that makes a raw JSON preview hard to read. */
function previewArgs(raw: string | null | undefined): string {
  if (!raw) return ''
  const cleaned = raw
    .replace(/\s+/gu, ' ')
    .replace(/^[{\["']+/u, '')
    .replace(/[}\]"']+$/u, '')
    .trim()
  return truncate(cleaned, ARGS_PREVIEW_MAX)
}

export const fallbackFormatter: ToolFormatter = (event): FormatterOutput => {
  const tool = event.name || 'tool'
  const preview = previewArgs(event.args)

  return {
    activeLabel: preview
      ? `Using \`${tool}\` · ${preview}`
      : `Using \`${tool}\``,
    summary: `Used \`${tool}\` — done`,
    errorVariant: `Used \`${tool}\` — failed`,
    glyph: 'Build',
  }
}

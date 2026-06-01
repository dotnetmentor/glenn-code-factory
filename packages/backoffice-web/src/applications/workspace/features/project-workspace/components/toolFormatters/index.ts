/**
 * Tool formatter registry — the public entry point for card 5 of
 * cursor-native-chat-ux §4.
 *
 * <p>Given a {@link ToolUseEventDto}, {@link formatTool} returns three
 * render-ready strings (active label, completed summary, error variant) plus
 * an optional MUI icon name. The chat pill (card 6) and the expanded trace
 * (card 7) consume these as the only allowed source of truth — raw JSON is
 * still reachable via "view raw", but never default.</p>
 *
 * <p><b>Registration model.</b> Each formatter is registered under every
 * tool name the Cursor SDK is known to emit for the same concept (Cursor's
 * own snake_case, Claude's PascalCase, and OpenCode's lowercase aliases).
 * Lookup is case-sensitive — adding a new alias is one entry in the
 * {@code REGISTRY} map below.</p>
 *
 * <p>Unknown {@code event.name} values fall through to
 * {@link fallbackFormatter}. The function never throws; even if a formatter
 * does, we catch and degrade to the fallback so the chat surface stays
 * alive.</p>
 */
import type { FormatterOutput, ToolFormatter, ToolUseEventDto } from './types'
import { editFormatter } from './edit'
import { fallbackFormatter } from './fallback'
import { globFormatter } from './glob'
import { grepFormatter } from './grep'
import { readFormatter } from './read'
import { shellFormatter } from './shell'
import { webfetchFormatter } from './webfetch'
import { writeFormatter } from './write'

/**
 * Registry of tool name → formatter. Names are listed verbatim — Cursor SDK
 * snake_case, Claude PascalCase, and any other alias spotted in the wild.
 */
const REGISTRY: Readonly<Record<string, ToolFormatter>> = Object.freeze({
  // shell / bash
  shell: shellFormatter,
  Bash: shellFormatter,
  bash: shellFormatter,
  run_terminal_cmd: shellFormatter,

  // read
  read: readFormatter,
  Read: readFormatter,
  read_file: readFormatter,

  // write
  write: writeFormatter,
  Write: writeFormatter,
  write_file: writeFormatter,

  // edit
  edit: editFormatter,
  Edit: editFormatter,
  MultiEdit: editFormatter,
  apply_edit: editFormatter,

  // grep / search
  grep: grepFormatter,
  Grep: grepFormatter,
  search: grepFormatter,

  // glob / ls
  glob: globFormatter,
  Glob: globFormatter,
  ls: globFormatter,
  LS: globFormatter,
  list_files: globFormatter,

  // web fetch
  webfetch: webfetchFormatter,
  WebFetch: webfetchFormatter,
  web_fetch: webfetchFormatter,
  fetch: webfetchFormatter,
})

/**
 * Public lookup. Returns the registered formatter's output, or the fallback
 * when no formatter matches. Wraps every call in a try/catch so a buggy
 * formatter can never crash the chat surface — defects degrade to the
 * generic "Used `<tool>`" treatment.
 */
export function formatTool(event: ToolUseEventDto): FormatterOutput {
  const formatter = REGISTRY[event.name] ?? fallbackFormatter
  try {
    return formatter(event)
  } catch {
    // A formatter threw despite the defensive helpers — fall all the way
    // back to the generic treatment so the UI keeps rendering.
    try {
      return fallbackFormatter(event)
    } catch {
      return {
        activeLabel: `Using \`${event.name || 'tool'}\``,
        summary: `Used \`${event.name || 'tool'}\` — done`,
        errorVariant: `Used \`${event.name || 'tool'}\` — failed`,
        glyph: 'Build',
      }
    }
  }
}

/**
 * Re-export the registry keys so consumers (tests, debug UIs) can introspect
 * which tool names are supported without poking at internals.
 */
export function registeredToolNames(): readonly string[] {
  return Object.keys(REGISTRY)
}

export type { FormatterOutput, ToolFormatter, ToolUseEventDto } from './types'
export { fallbackFormatter } from './fallback'

/**
 * Smoke tests for the tool formatter registry.
 *
 * <p>Each formatter has three assertions:
 * <ol>
 *   <li><b>Happy path</b> — realistic {@code args}+{@code result} payload
 *       produces the spec-§4 wording.</li>
 *   <li><b>Defensive</b> — only {@code name} + {@code status} present, no
 *       {@code args}, no {@code result} — must NOT throw and must still
 *       produce non-empty labels.</li>
 *   <li><b>Error variant</b> — error label renders without throwing.</li>
 * </ol></p>
 */
import { describe, expect, it } from 'vitest'

import {
  fallbackFormatter,
  formatTool,
  registeredToolNames,
} from '../index'
import type { ToolUseEventDto } from '../types'

/**
 * Build a minimal valid {@link ToolUseEventDto}. Tests override the fields
 * they care about; the rest stay at safe defaults.
 */
function makeEvent(overrides: Partial<ToolUseEventDto> = {}): ToolUseEventDto {
  return {
    sessionId: 'sess-1',
    sequence: 1,
    createdAt: '2026-01-01T00:00:00Z',
    callId: 'call-1',
    name: 'shell',
    status: 'Running',
    args: null,
    result: null,
    argsTruncated: false,
    resultTruncated: false,
    eventKind: 'toolUse',
    ...overrides,
  }
}

describe('formatTool — shell', () => {
  it('renders happy path with exit code + duration', () => {
    const event = makeEvent({
      name: 'shell',
      status: 'Completed',
      args: JSON.stringify({ command: 'npm run build' }),
      result: JSON.stringify({ exitCode: 0, durationMs: 1400 }),
    })
    const out = formatTool(event)
    expect(out.activeLabel).toBe('Running `npm run build`')
    expect(out.summary).toBe('Ran `npm run build` — exit 0 (1.4s)')
    expect(out.errorVariant).toBe('Ran `npm run build` — exit 0')
    expect(out.glyph).toBe('Terminal')
  })

  it('truncates long commands at ~60 chars', () => {
    const longCmd = 'echo ' + 'x'.repeat(120)
    const event = makeEvent({ name: 'shell', args: JSON.stringify({ command: longCmd }) })
    const out = formatTool(event)
    // Active label includes the backticks + "Running " prefix, so total
    // length is bounded by ~60 + decoration.
    expect(out.activeLabel.length).toBeLessThanOrEqual('Running ``'.length + 60)
    expect(out.activeLabel.endsWith('…`')).toBe(true)
  })

  it('survives missing args/result without throwing', () => {
    const event = makeEvent({ name: 'shell' })
    const out = formatTool(event)
    expect(out.activeLabel).toContain('Running')
    expect(out.summary).toContain('Ran')
    expect(out.errorVariant).toContain('failed')
  })

  it('renders error variant when only exitCode known', () => {
    const event = makeEvent({
      name: 'bash',
      status: 'Error',
      args: JSON.stringify({ command: 'false' }),
      result: JSON.stringify({ exitCode: 1 }),
    })
    expect(formatTool(event).errorVariant).toBe('Ran `false` — exit 1')
  })
})

describe('formatTool — read', () => {
  it('renders happy path with line count', () => {
    const event = makeEvent({
      name: 'read',
      status: 'Completed',
      args: JSON.stringify({ path: 'src/auth/SessionService.cs' }),
      result: JSON.stringify({ lineCount: 412 }),
    })
    const out = formatTool(event)
    expect(out.activeLabel).toBe('Reading `src/auth/SessionService.cs`')
    expect(out.summary).toBe('Read 412 lines from `SessionService.cs`')
    expect(out.glyph).toBe('Description')
  })

  it('error variant carries reason from result.error', () => {
    const event = makeEvent({
      name: 'Read',
      status: 'Error',
      args: JSON.stringify({ file_path: '/tmp/missing.txt' }),
      result: JSON.stringify({ error: 'file not found' }),
    })
    expect(formatTool(event).errorVariant).toBe(
      "Couldn't read `missing.txt` — file not found",
    )
  })

  it('survives missing args/result', () => {
    const event = makeEvent({ name: 'read_file' })
    const out = formatTool(event)
    expect(out.activeLabel).toContain('Reading')
    expect(out.summary).toContain('Read')
  })
})

describe('formatTool — write', () => {
  it('renders happy path with explicit lineCount', () => {
    const event = makeEvent({
      name: 'write',
      args: JSON.stringify({ path: 'components/Button.tsx' }),
      result: JSON.stringify({ lineCount: 47 }),
    })
    const out = formatTool(event)
    expect(out.activeLabel).toBe('Writing `Button.tsx`')
    expect(out.summary).toBe('Wrote `Button.tsx` (47 lines)')
    expect(out.errorVariant).toBe('Write failed for `Button.tsx`')
    expect(out.glyph).toBe('NoteAdd')
  })

  it('falls back to counting content newlines when result lacks lineCount', () => {
    const event = makeEvent({
      name: 'Write',
      args: JSON.stringify({ path: 'a.txt', content: 'one\ntwo\nthree' }),
    })
    expect(formatTool(event).summary).toBe('Wrote `a.txt` (3 lines)')
  })

  it('survives missing args/result', () => {
    const event = makeEvent({ name: 'write_file' })
    const out = formatTool(event)
    expect(out.activeLabel).toContain('Writing')
    expect(out.summary).toContain('Wrote')
  })
})

describe('formatTool — edit', () => {
  it('renders +added −removed diff segment', () => {
    const event = makeEvent({
      name: 'edit',
      args: JSON.stringify({ path: 'src/components/App.tsx' }),
      result: JSON.stringify({ added: 24, removed: 7 }),
    })
    const out = formatTool(event)
    expect(out.activeLabel).toBe('Editing `App.tsx`')
    expect(out.summary).toBe('Edited `App.tsx` (+24 −7)')
    expect(out.errorVariant).toBe('Edit failed for `App.tsx`')
    expect(out.glyph).toBe('Edit')
  })

  it('falls back to plain "Edited" when diff counts missing', () => {
    const event = makeEvent({
      name: 'MultiEdit',
      args: JSON.stringify({ filePath: 'x.ts' }),
    })
    expect(formatTool(event).summary).toBe('Edited `x.ts`')
  })

  it('survives missing args/result', () => {
    const event = makeEvent({ name: 'apply_edit' })
    const out = formatTool(event)
    expect(out.activeLabel).toContain('Editing')
    expect(out.summary).toContain('Edited')
  })
})

describe('formatTool — grep', () => {
  it('renders happy path with match + file counts', () => {
    const event = makeEvent({
      name: 'grep',
      args: JSON.stringify({ pattern: 'useEffect' }),
      result: JSON.stringify({ matches: 17, files: 6 }),
    })
    const out = formatTool(event)
    expect(out.activeLabel).toBe('Searching for `useEffect`')
    expect(out.summary).toBe('Found 17 matches across 6 files for `useEffect`')
    expect(out.errorVariant).toBe('Search failed')
    expect(out.glyph).toBe('Search')
  })

  it('renders "No matches" when matches=0', () => {
    const event = makeEvent({
      name: 'Grep',
      args: JSON.stringify({ pattern: 'xyzNotFound' }),
      result: JSON.stringify({ matches: 0, files: 0 }),
    })
    expect(formatTool(event).summary).toBe('No matches for `xyzNotFound`')
  })

  it('survives missing args/result', () => {
    const event = makeEvent({ name: 'search' })
    const out = formatTool(event)
    expect(out.activeLabel).toContain('Searching')
    expect(out.summary).toContain('no pattern')
  })
})

describe('formatTool — glob / ls', () => {
  it('renders entry count', () => {
    const event = makeEvent({
      name: 'glob',
      args: JSON.stringify({ path: 'src/' }),
      result: JSON.stringify({ entries: 23 }),
    })
    const out = formatTool(event)
    expect(out.activeLabel).toBe('Listing `src/`')
    expect(out.summary).toBe('Listed 23 entries in `src/`')
    expect(out.errorVariant).toBe('Path not found: `src/`')
    expect(out.glyph).toBe('FolderOpen')
  })

  it('survives missing args/result for LS', () => {
    const event = makeEvent({ name: 'LS' })
    const out = formatTool(event)
    expect(out.activeLabel).toContain('Listing')
    expect(out.summary).toContain('Listed')
  })
})

describe('formatTool — webfetch', () => {
  it('renders hostname + char count', () => {
    const event = makeEvent({
      name: 'webfetch',
      args: JSON.stringify({ url: 'https://example.com/api/v1/items?q=1' }),
      result: JSON.stringify({ chars: 2048 }),
    })
    const out = formatTool(event)
    expect(out.activeLabel).toBe('Fetching example.com')
    expect(out.summary).toBe('Fetched example.com (2048 chars)')
    expect(out.errorVariant).toBe('Fetch failed: example.com')
    expect(out.glyph).toBe('Public')
  })

  it('measures body length when chars missing', () => {
    const event = makeEvent({
      name: 'WebFetch',
      args: JSON.stringify({ url: 'https://foo.test/x' }),
      result: JSON.stringify({ body: 'hello world' }),
    })
    expect(formatTool(event).summary).toBe('Fetched foo.test (11 chars)')
  })

  it('survives missing args/result', () => {
    const event = makeEvent({ name: 'fetch' })
    const out = formatTool(event)
    expect(out.activeLabel).toContain('Fetching')
    expect(out.summary).toContain('Fetched')
  })
})

describe('formatTool — fallback', () => {
  it('renders for unknown tool names with args preview', () => {
    const event = makeEvent({
      name: 'mystery_tool',
      args: JSON.stringify({ thing: 'banana', count: 3 }),
    })
    const out = formatTool(event)
    expect(out.activeLabel.startsWith('Using `mystery_tool` · ')).toBe(true)
    expect(out.summary).toBe('Used `mystery_tool` — done')
    expect(out.errorVariant).toBe('Used `mystery_tool` — failed')
    expect(out.glyph).toBe('Build')
  })

  it('truncates very long args previews to 80 chars', () => {
    const huge = 'a'.repeat(500)
    const event = makeEvent({
      name: 'mystery_tool',
      args: JSON.stringify({ giant: huge }),
    })
    const out = formatTool(event)
    // Active label = "Using `mystery_tool` · " + at most 80 chars of preview.
    const prefix = 'Using `mystery_tool` · '
    expect(out.activeLabel.length).toBeLessThanOrEqual(prefix.length + 80)
  })

  it('renders without args at all', () => {
    const event = makeEvent({ name: 'unknown' })
    const out = fallbackFormatter(event)
    expect(out.activeLabel).toBe('Using `unknown`')
    expect(out.summary).toBe('Used `unknown` — done')
    expect(out.errorVariant).toBe('Used `unknown` — failed')
  })

  it('falls back when name is empty string', () => {
    const event = makeEvent({ name: '' })
    const out = formatTool(event)
    // Empty name → fallback uses the literal "tool" stand-in.
    expect(out.activeLabel).toContain('tool')
    expect(out.summary).toContain('tool')
  })
})

describe('formatTool — registry behavior', () => {
  it('never throws for arbitrary garbage in args/result', () => {
    const event = makeEvent({
      name: 'shell',
      args: 'not valid json {{{',
      result: '[also not json',
    })
    expect(() => formatTool(event)).not.toThrow()
  })

  it('registry exposes its known names', () => {
    const names = registeredToolNames()
    expect(names).toContain('shell')
    expect(names).toContain('Bash')
    expect(names).toContain('Read')
    expect(names).toContain('Write')
    expect(names).toContain('Edit')
    expect(names).toContain('Grep')
    expect(names).toContain('Glob')
    expect(names).toContain('WebFetch')
  })

  it('always returns non-empty labels', () => {
    const event = makeEvent({ name: 'totally_made_up' })
    const out = formatTool(event)
    expect(out.activeLabel.length).toBeGreaterThan(0)
    expect(out.summary.length).toBeGreaterThan(0)
    expect(out.errorVariant.length).toBeGreaterThan(0)
  })
})

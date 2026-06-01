// readLogTail — read the last N lines of a log file from the end, without
// loading the whole file into memory.
//
// === Use case ===
//
// Service failure events (`ServiceFailedToStart`, `ServiceCrashed`) ride a
// short stderr tail in their payload so the super-admin Timeline shows the
// smoking gun inline. Supervisord rotates each service's stderr at 10 MB and
// keeps a few generations on disk, so a naive `fs.readFile` would pull
// megabytes into memory just to throw away everything but the last 50 lines.
//
// === Algorithm ===
//
// 1. Open the file, stat it.
// 2. Walk backward in fixed-size chunks (8 KB) from the end.
// 3. Decode each chunk as UTF-8, prepend to an in-memory buffer.
// 4. Count newlines. When we have `lineCount + 1` newlines or hit BOF, stop.
// 5. Split the buffer on `\n`, drop the trailing empty (file usually ends in
//    a newline), take the last `lineCount` lines.
// 6. Per-line cap: trim each line to `maxLineBytes` characters with a
//    truncation marker so a pathological log spewing megabyte-long lines
//    can't ride into the event payload.
//
// === Caps ===
//
// Defaults: 50 lines × ~500 bytes per line = 25 KB max. The caller (the
// service status poller) attaches this to an event payload that is itself
// capped at 16 KB by `RuntimeEventEmitter`'s truncation step — so the
// worst-case stderr tail going onto the wire is bounded twice: once here
// and once at the emitter.
//
// === Fault tolerance ===
//
// File missing / permission denied / open failure → returns `[]`. The caller
// treats absence-of-tail as "we tried, nothing to show" and keeps emitting the
// event. We deliberately don't surface the error; observability must never
// fail the operation it's observing.

import {
  open as fsOpen,
  type FileHandle,
} from 'node:fs/promises'

const DEFAULT_LINE_COUNT = 50
const DEFAULT_MAX_LINE_BYTES = 500
const READ_CHUNK_BYTES = 8 * 1024
/** Hard cap on bytes read. Even if the file has no newlines at all we won't load >256KB. */
const HARD_BYTE_CAP = 256 * 1024

export interface ReadLogTailOptions {
  /** How many lines to return at most. Default 50. */
  lineCount?: number
  /** Per-line byte cap; longer lines are truncated and marked. Default 500. */
  maxLineBytes?: number
}

/**
 * Read up to `lineCount` lines from the end of `filePath`. Never throws —
 * returns `[]` if the file can't be read for any reason.
 */
export async function readLogTail(
  filePath: string,
  options: ReadLogTailOptions = {},
): Promise<string[]> {
  const lineCount = options.lineCount ?? DEFAULT_LINE_COUNT
  const maxLineBytes = options.maxLineBytes ?? DEFAULT_MAX_LINE_BYTES
  if (lineCount <= 0) return []

  let handle: FileHandle | undefined
  try {
    handle = await fsOpen(filePath, 'r')
    const stat = await handle.stat()
    let position = stat.size
    if (position <= 0) return []

    let buffer = ''
    let bytesRead = 0

    while (position > 0 && bytesRead < HARD_BYTE_CAP) {
      const chunkSize = Math.min(READ_CHUNK_BYTES, position)
      const start = position - chunkSize
      const buf = Buffer.alloc(chunkSize)
      await handle.read(buf, 0, chunkSize, start)
      buffer = buf.toString('utf8') + buffer
      bytesRead += chunkSize
      position = start
      // Count newlines (excluding any trailing single newline on the file).
      const newlineCount = countChar(buffer, '\n')
      // Need lineCount + 1 newlines to fully demarcate `lineCount` complete
      // lines from the end (the +1 ensures we caught the boundary of the
      // earliest line we'll keep).
      if (newlineCount > lineCount) break
    }

    return finaliseLines(buffer, lineCount, maxLineBytes)
  } catch {
    // Any failure (ENOENT, EACCES, EISDIR, …): observability degrades to "no
    // tail available", caller still emits its event.
    return []
  } finally {
    if (handle !== undefined) {
      try {
        await handle.close()
      } catch {
        // ignore close failures — nothing actionable here.
      }
    }
  }
}

function countChar(s: string, ch: string): number {
  let n = 0
  for (let i = 0; i < s.length; i += 1) {
    if (s[i] === ch) n += 1
  }
  return n
}

function finaliseLines(buffer: string, lineCount: number, maxLineBytes: number): string[] {
  // Trim a single trailing newline (it would otherwise produce a phantom
  // empty last line). We don't trim runs — multiple blank lines at end
  // probably mean something to whoever wrote the log.
  let cleaned = buffer
  if (cleaned.endsWith('\n')) cleaned = cleaned.slice(0, -1)
  const split = cleaned.split('\n')
  const tail = split.length > lineCount ? split.slice(split.length - lineCount) : split
  return tail.map((line) => clampLine(line, maxLineBytes))
}

function clampLine(line: string, maxLineBytes: number): string {
  if (line.length <= maxLineBytes) return line
  // Length is roughly proportional to bytes for ASCII-heavy logs (supervisord
  // stderr generally is). Trim by character count for predictability — exact
  // byte trimming would risk slicing in the middle of a UTF-8 codepoint.
  return line.slice(0, maxLineBytes) + '… [truncated]'
}

/**
 * Formatter for shell-like tools (Cursor SDK names: {@code shell},
 * {@code Bash}, {@code bash}, {@code run_terminal_cmd}).
 *
 * <p>Reads the command from {@code args.command} (or {@code args.cmd}) and,
 * once finished, surfaces the exit code + run duration from {@code result}.
 * The command is truncated at 60 characters in every label so a multi-line
 * pipeline doesn't blow out the pill.</p>
 */
import type { FormatterOutput, ToolFormatter } from './types'
import {
  formatDuration,
  pickNumber,
  pickString,
  safeParseRecord,
  truncate,
} from './helpers'

const CMD_MAX = 60

export const shellFormatter: ToolFormatter = (event): FormatterOutput => {
  const args = safeParseRecord(event.args)
  const result = safeParseRecord(event.result)

  const rawCmd =
    pickString(args, ['command', 'cmd', 'shell', 'script']) ?? '(no command)'
  const cmd = truncate(rawCmd, CMD_MAX)

  const exitCode = pickNumber(result, ['exitCode', 'exit_code', 'exit', 'code'])
  const duration = formatDuration(
    pickNumber(result, ['durationMs', 'duration_ms', 'elapsedMs', 'elapsed_ms']),
  )

  const tail = [
    exitCode !== undefined ? `exit ${exitCode}` : undefined,
    duration ? `(${duration})` : undefined,
  ]
    .filter(Boolean)
    .join(' ')

  return {
    activeLabel: `Running \`${cmd}\``,
    summary: tail ? `Ran \`${cmd}\` — ${tail}` : `Ran \`${cmd}\``,
    errorVariant:
      exitCode !== undefined
        ? `Ran \`${cmd}\` — exit ${exitCode}`
        : `Ran \`${cmd}\` — failed`,
    glyph: 'Terminal',
  }
}

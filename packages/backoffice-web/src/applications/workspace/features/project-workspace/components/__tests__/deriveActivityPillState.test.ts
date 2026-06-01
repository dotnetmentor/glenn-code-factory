/**
 * deriveActivityPillState — branch-coverage unit tests for the pure projection
 * function from {@link Message[]} → {@link ActivityPillState} (card 7,
 * cursor-native-chat-ux §3).
 *
 * <p>One {@code it()} per priority rule in the derivation:</p>
 * <ol>
 *   <li>Empty input → {@code creating}.</li>
 *   <li>Status-only branches: {@code Creating}, {@code Running} (with /
 *       without task title), {@code Finished} (no tool).</li>
 *   <li>Terminal run failures supersede tool state: {@code Cancelled},
 *       {@code Error}, {@code Expired}.</li>
 *   <li>Latest non-status is a tool:
 *       Running → {@code tool-running} with live elapsed,
 *       Completed → {@code tool-completed},
 *       Error → {@code tool-error}.</li>
 *   <li>Latest non-status is a thinking → {@code thinking}.</li>
 *   <li>Frozen turn: {@code Finished} status + earlier tool → pass through
 *       the LAST tool's frozen state.</li>
 *   <li>Frozen turn: {@code Finished} status + thinking only → {@code thinking}
 *       with canonical {@code thinkingDurationMs}.</li>
 * </ol>
 *
 * <p>The injected {@code now} pins the wall clock so live elapsed assertions
 * are deterministic.</p>
 */
import { describe, expect, it } from 'vitest'
import { deriveActivityPillState } from '../deriveActivityPillState'
import type { Message } from '../chatEvents'

// ── Test fixtures ──────────────────────────────────────────────────────────

const NOW = Date.parse('2026-01-01T00:00:10.000Z')
const TEN_S_AGO_ISO = '2026-01-01T00:00:00.000Z' // 10s before NOW

function status(
  status: Extract<Message, { kind: 'status' }>['status'],
  seq = 1,
  message: string | null = null,
): Extract<Message, { kind: 'status' }> {
  return {
    kind: 'status',
    id: `s:${seq}`,
    sessionId: 's',
    sequence: seq,
    status,
    message,
    createdAt: TEN_S_AGO_ISO,
  }
}

function tool(
  toolStatus: 'Running' | 'Completed' | 'Error',
  seq = 1,
  name = 'shell',
  args: string | null = '{"cmd":"npm run build"}',
  result: string | null = null,
  createdAt: string = TEN_S_AGO_ISO,
): Extract<Message, { kind: 'tool' }> {
  return {
    kind: 'tool',
    id: `s:${seq}`,
    sessionId: 's',
    sequence: seq,
    callId: `call-${seq}`,
    name,
    toolStatus,
    args,
    result,
    argsTruncated: false,
    resultTruncated: false,
    createdAt,
  }
}

function thinking(
  seq = 1,
  text = 'pondering',
  thinkingDurationMs: number | null = null,
  createdAt: string = TEN_S_AGO_ISO,
): Extract<Message, { kind: 'thinking' }> {
  return {
    kind: 'thinking',
    id: `s:${seq}`,
    sessionId: 's',
    sequence: seq,
    text,
    thinkingDurationMs,
    createdAt,
  }
}

function task(
  seq = 1,
  title = 'Refactor module',
): Extract<Message, { kind: 'task' }> {
  return {
    kind: 'task',
    id: `s:${seq}`,
    sessionId: 's',
    sequence: seq,
    taskId: 'task-1',
    title,
    createdAt: TEN_S_AGO_ISO,
  }
}

// ── Tests ──────────────────────────────────────────────────────────────────

describe('deriveActivityPillState — empty + status-only branches', () => {
  it('empty messages → creating', () => {
    const out = deriveActivityPillState([], true, { now: NOW })
    expect(out.kind).toBe('creating')
  })

  it('Creating status → creating', () => {
    const out = deriveActivityPillState([status('Creating')], true, { now: NOW })
    expect(out.kind).toBe('creating')
  })

  it('Running status only (no task) → running-idle', () => {
    const out = deriveActivityPillState([status('Running')], true, { now: NOW })
    expect(out.kind).toBe('running-idle')
    if (out.kind === 'running-idle') {
      expect(out.taskTitle).toBeUndefined()
    }
  })

  it('Running status with task message → running-idle carries task title', () => {
    const out = deriveActivityPillState(
      [task(1, 'Refactor module'), status('Running', 2)],
      true,
      { now: NOW },
    )
    expect(out.kind).toBe('running-idle')
    if (out.kind === 'running-idle') {
      expect(out.taskTitle).toBe('Refactor module')
    }
  })

  it('Finished status with no tool / thinking → running-idle (quiet frozen)', () => {
    const out = deriveActivityPillState([status('Finished')], false, {
      now: NOW,
    })
    expect(out.kind).toBe('running-idle')
  })
})

describe('deriveActivityPillState — run-level terminal failures', () => {
  it('Cancelled status supersedes any prior tool → cancelled', () => {
    const out = deriveActivityPillState(
      [tool('Running', 1), status('Cancelled', 2)],
      false,
      { now: NOW },
    )
    expect(out.kind).toBe('cancelled')
  })

  it('Error status → run-error', () => {
    const out = deriveActivityPillState(
      [tool('Running', 1), status('Error', 2)],
      false,
      { now: NOW },
    )
    expect(out.kind).toBe('run-error')
  })

  it('Expired status → expired', () => {
    const out = deriveActivityPillState([status('Expired')], false, {
      now: NOW,
    })
    expect(out.kind).toBe('expired')
  })

  it('Cancelled followed by Error → cancelled (Cancelled is sticky vs late daemon race)', () => {
    // Repro the actual on-wire sequence: user cancels a Pending turn, our
    // backend emits a synthetic Cancelled status, then the daemon's late
    // Error event arrives because it was mid-flight when cancel landed.
    // Pill must read Cancelled, not Error.
    const out = deriveActivityPillState(
      [
        tool('Running', 1),
        status('Cancelled', 2),
        status('Error', 3),
      ],
      false,
      { now: NOW },
    )
    expect(out.kind).toBe('cancelled')
  })

  it('Error then Cancelled → run-error (genuine error preserved if it landed first)', () => {
    // Inverse case — if a real failure happened BEFORE any cancel, the
    // failure is the truth. We're not silencing genuine errors, only
    // respecting user-cancel intent against late daemon noise.
    const out = deriveActivityPillState(
      [
        tool('Running', 1),
        status('Error', 2),
        status('Cancelled', 3),
      ],
      false,
      { now: NOW },
    )
    expect(out.kind).toBe('run-error')
  })

  it('Expired followed by Error → expired (Expired is also sticky)', () => {
    const out = deriveActivityPillState(
      [
        status('Running', 1),
        status('Expired', 2),
        status('Error', 3),
      ],
      false,
      { now: NOW },
    )
    expect(out.kind).toBe('expired')
  })
})

describe('deriveActivityPillState — latest non-status is a tool', () => {
  it('Running tool → tool-running with elapsed', () => {
    const out = deriveActivityPillState([tool('Running')], true, { now: NOW })
    expect(out.kind).toBe('tool-running')
    if (out.kind === 'tool-running') {
      // NOW − TEN_S_AGO_ISO == 10 000 ms.
      expect(out.elapsedMs).toBe(10_000)
      expect(out.formatted).toBeDefined()
    }
  })

  it('Running tool on a frozen turn → elapsedMs is 0 (no live counter)', () => {
    const out = deriveActivityPillState([tool('Running')], false, { now: NOW })
    expect(out.kind).toBe('tool-running')
    if (out.kind === 'tool-running') {
      expect(out.elapsedMs).toBe(0)
    }
  })

  it('Completed tool → tool-completed', () => {
    const out = deriveActivityPillState(
      [tool('Completed', 1, 'shell', '{}', 'ok')],
      true,
      { now: NOW },
    )
    expect(out.kind).toBe('tool-completed')
  })

  it('Error tool → tool-error', () => {
    const out = deriveActivityPillState(
      [tool('Error', 1, 'shell', '{}', 'boom')],
      true,
      { now: NOW },
    )
    expect(out.kind).toBe('tool-error')
  })
})

describe('deriveActivityPillState — thinking branches', () => {
  it('Latest thinking with no follow-up → thinking with live elapsed', () => {
    const out = deriveActivityPillState([thinking()], true, { now: NOW })
    expect(out.kind).toBe('thinking')
    if (out.kind === 'thinking') {
      expect(out.elapsedMs).toBe(10_000)
    }
  })

  it('Frozen thinking with canonical duration → thinking-done with duration', () => {
    const out = deriveActivityPillState(
      [thinking(1, 'pondering', 4_200)],
      false,
      { now: NOW },
    )
    // Frozen phases should past-tense the pill so it doesn't shimmer forever.
    expect(out.kind).toBe('thinking-done')
    if (out.kind === 'thinking-done') {
      expect(out.elapsedMs).toBe(4_200)
    }
  })

  it('Frozen thinking with no duration → thinking-done with elapsedMs 0', () => {
    const out = deriveActivityPillState([thinking()], false, { now: NOW })
    expect(out.kind).toBe('thinking-done')
    if (out.kind === 'thinking-done') {
      expect(out.elapsedMs).toBe(0)
    }
  })
})

describe('deriveActivityPillState — finished turns pass through last tool', () => {
  it('Finished + prior tool + assistant text → frozen on the LAST tool', () => {
    const out = deriveActivityPillState(
      [
        tool('Completed', 1, 'shell', '{}', 'ok'),
        {
          kind: 'assistant',
          id: 's:2',
          sessionId: 's',
          sequence: 2,
          text: 'All done.',
          createdAt: TEN_S_AGO_ISO,
        },
        status('Finished', 3),
      ],
      false,
      { now: NOW },
    )
    // Latest non-status is the assistant message, so we fall through to
    // the "frozen tool" branch.
    expect(out.kind).toBe('tool-completed')
  })

  it('Finished + thinking only (no tool) → thinking-done with canonical duration', () => {
    const out = deriveActivityPillState(
      [thinking(1, 'pondering', 8_000), status('Finished', 2)],
      false,
      { now: NOW },
    )
    expect(out.kind).toBe('thinking-done')
    if (out.kind === 'thinking-done') {
      expect(out.elapsedMs).toBe(8_000)
    }
  })

  it('Finished + Error tool → tool-error (Error wins over Completed)', () => {
    const out = deriveActivityPillState(
      [
        tool('Error', 1, 'shell', '{}', 'boom'),
        status('Finished', 2),
      ],
      false,
      { now: NOW },
    )
    expect(out.kind).toBe('tool-error')
  })
})

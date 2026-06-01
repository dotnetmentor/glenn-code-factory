/**
 * deriveActivityPillState — pure projection from a turn's {@link Message}
 * stream to the {@link ActivityPillState} discriminated union the
 * {@link ActivityPill} renders (cursor-native-chat-ux §3, card 7).
 *
 * <p>One pill per assistant turn. The LATEST live turn's state animates as
 * events arrive. OLDER, terminal turns freeze at the LAST tool / thinking
 * state — so scrolling history shows what each turn did at a glance.</p>
 *
 * <h3>Derivation rules (in priority order, walking the {@link Message[]}
 * from the END backwards):</h3>
 * <ol>
 *   <li>If a {@code StatusEvent} carrying a terminal RUN-level state
 *       ({@code Cancelled} / {@code Error} / {@code Expired}) is the LAST
 *       status, surface the matching pill state — these are run-level
 *       failures and supersede any preceding tool state.</li>
 *   <li>If the latest non-status event is a {@code ToolUseEvent}:
 *     <ul>
 *       <li>{@code Running} → {@code tool-running} with {@code elapsedMs}
 *           (live: {@code now - createdAt}; frozen: 0 — no longer animating).</li>
 *       <li>{@code Completed} → {@code tool-completed} with the formatter
 *           output.</li>
 *       <li>{@code Error} → {@code tool-error}.</li>
 *     </ul>
 *   </li>
 *   <li>If the latest non-status event is a {@code ThinkingEvent} that hasn't
 *       been followed by a completion event → {@code thinking} with
 *       {@code elapsedMs}.</li>
 *   <li>If most recent status is {@code Finished} and there's a previous
 *       tool/thinking — surface that LAST tool's frozen state (pass-through
 *       to the {@code tool-completed} / {@code tool-error} of the last tool).
 *       Spec §3 final paragraph: "the LAST tool's frozen state, so scrolling
 *       history reads as 'this turn ended with this tool'".</li>
 *   <li>If most recent status is {@code Running} and no tool currently running
 *       → {@code running-idle} with the latest task title (if any).</li>
 *   <li>If most recent status is {@code Creating} → {@code creating}.</li>
 *   <li>Fallback (no events at all) → {@code creating}.</li>
 * </ol>
 *
 * <p>This function is intentionally pure — no React, no timers, no side
 * effects — so it can be unit-tested in isolation. The caller hosts the
 * {@code useElapsedMs} hook for live turns to make the {@code tool-running} /
 * {@code thinking} counter tick.</p>
 */
import type { Message } from './chatEvents'
import { formatTool } from './toolFormatters'
import type { FormatterOutput, ToolUseEventDto } from './toolFormatters'
import type { ActivityPillState } from './ActivityPill'

/**
 * Reconstruct a minimal {@link ToolUseEventDto} from a {@code Message} tool
 * row so we can re-use the formatter registry. The Message intentionally
 * drops the {@code eventKind} discriminator — the formatter ignores it, so
 * we don't need to fabricate it back.
 */
function messageToToolEvent(
  m: Extract<Message, { kind: 'tool' }>,
): ToolUseEventDto {
  return {
    sessionId: m.sessionId,
    sequence: m.sequence,
    createdAt: m.createdAt ?? '',
    callId: m.callId,
    name: m.name,
    status: m.toolStatus,
    args: m.args,
    result: m.result,
    argsTruncated: m.argsTruncated,
    resultTruncated: m.resultTruncated,
    eventKind: 'toolUse',
  }
}

/**
 * Parse a Message's {@code createdAt} ISO string into wall-clock ms. Returns
 * {@code undefined} for missing / unparseable timestamps so the caller can
 * fall back to "no elapsed counter".
 */
function parseCreatedMs(iso: string | undefined): number | undefined {
  if (!iso) return undefined
  const ms = Date.parse(iso)
  return Number.isFinite(ms) ? ms : undefined
}

/**
 * Find the most recent message of a given kind. Returns {@code undefined}
 * if none. We walk backwards — chronological insertion order is preserved
 * by the caller (ChatCanvas sorts by sessionId.createdAt then sequence).
 */
function findLast<K extends Message['kind']>(
  messages: readonly Message[],
  kind: K,
): Extract<Message, { kind: K }> | undefined {
  for (let i = messages.length - 1; i >= 0; i--) {
    const m = messages[i]
    if (m.kind === kind) return m as Extract<Message, { kind: K }>
  }
  return undefined
}

/**
 * Build the {@code tool-completed} or {@code tool-error} state for a frozen
 * terminal tool — used both for the "tool finished, next not yet started"
 * case and for the "turn finished, surface the LAST tool's frozen state"
 * case (spec §3 final paragraph).
 */
function frozenToolState(
  toolMsg: Extract<Message, { kind: 'tool' }>,
  formatted: FormatterOutput,
): ActivityPillState {
  if (toolMsg.toolStatus === 'Error') {
    return { kind: 'tool-error', formatted }
  }
  return { kind: 'tool-completed', formatted }
}

export interface DeriveActivityPillStateOptions {
  /**
   * Wall-clock "now" injected for tests. Defaults to {@link Date.now} —
   * production callers can omit it. Tests should pin it so elapsed
   * assertions are stable.
   */
  now?: number
}

/**
 * Map a turn's chronological {@link Message[]} to the pill state the
 * {@link ActivityPill} renders. See module docstring for the priority
 * rules.
 *
 * @param messages All messages for ONE turn, in chronological (sequence)
 *                 order. Multiple tool calls within the turn are present —
 *                 we use the LAST one. The caller has already filtered to
 *                 this turn's session.
 * @param isLiveTurn {@code true} when this is the most recent, still-streaming
 *                   turn. Drives elapsed-ms behaviour: live → use wall clock;
 *                   frozen → 0 (no animation).
 */
export function deriveActivityPillState(
  messages: readonly Message[],
  isLiveTurn: boolean,
  options: DeriveActivityPillStateOptions = {},
): ActivityPillState {
  const now = options.now ?? Date.now()

  // Scan for the latest status and tool/thinking by walking the array
  // backwards. We don't short-circuit on the first non-status hit because
  // the run-level terminal status SUPERSEDES any preceding tool state
  // (a Cancelled run should surface as "Cancelled" even if a tool was
  // running at the moment the cancel landed).
  const lastStatus = findLast(messages, 'status')
  const lastTool = findLast(messages, 'tool')
  const lastThinking = findLast(messages, 'thinking')
  const lastTask = findLast(messages, 'task')

  // Find the index of the last "live" event (tool / thinking / assistant
  // text) so we can decide whether thinking is "ongoing" (no later
  // completion event) vs already superseded by a tool call.
  let lastNonStatusIdx = -1
  for (let i = messages.length - 1; i >= 0; i--) {
    const m = messages[i]
    if (m.kind === 'tool' || m.kind === 'thinking' || m.kind === 'assistant') {
      lastNonStatusIdx = i
      break
    }
  }

  // ── 1. Run-level terminal failure modes (priority over tool state) ────
  //
  // We scan for the FIRST terminal status to arrive, not the last, because:
  //   - The user-cancel path emits a synthetic `Cancelled` status event the
  //     moment the cancel command lands; the daemon may THEN race in an
  //     `Error` event because it was mid-flight when the cancel arrived. The
  //     user's intent (`Cancelled`) is the truth — the daemon's late error
  //     is a race artifact that shouldn't poison the pill.
  //   - Symmetrically, an `Expired` followed by anything else is still
  //     Expired — the turn was ruled timed-out, period.
  //   - A genuine `Error` (no cancel happened) is the first terminal status
  //     in its stream, so the same scan returns `run-error` correctly.
  //
  // Status events are pushed in arrival order, so the first terminal one
  // in the list is the first to land on the wire.
  const terminalStatus = messages.find(
    (m) =>
      m.kind === 'status' &&
      (m.status === 'Cancelled' ||
        m.status === 'Error' ||
        m.status === 'Expired'),
  ) as Extract<Message, { kind: 'status' }> | undefined
  if (terminalStatus) {
    switch (terminalStatus.status) {
      case 'Cancelled':
        return { kind: 'cancelled' }
      case 'Error':
        return { kind: 'run-error' }
      case 'Expired':
        return { kind: 'expired' }
    }
  }

  // ── 2. The latest non-status event drives the active label ────────────
  const lastNonStatus =
    lastNonStatusIdx >= 0 ? messages[lastNonStatusIdx] : undefined

  if (lastNonStatus && lastNonStatus.kind === 'tool') {
    const toolEvent = messageToToolEvent(lastNonStatus)
    const formatted = formatTool(toolEvent)
    if (lastNonStatus.toolStatus === 'Running') {
      const startMs = parseCreatedMs(lastNonStatus.createdAt)
      // Live turn ticks via the caller's useElapsedMs hook; we seed with
      // the wall-clock diff here so the FIRST paint shows the correct
      // value (the hook only updates on its next interval tick).
      const elapsedMs =
        isLiveTurn && startMs !== undefined ? Math.max(0, now - startMs) : 0
      return { kind: 'tool-running', formatted, elapsedMs }
    }
    return frozenToolState(lastNonStatus, formatted)
  }

  if (lastNonStatus && lastNonStatus.kind === 'thinking') {
    const startMs = parseCreatedMs(lastNonStatus.createdAt)
    // If the thinking event carries its own duration AND this turn is
    // already terminal, prefer that — it's the canonical "thought for X"
    // measurement. Otherwise fall back to wall clock.
    const durationMs = lastNonStatus.thinkingDurationMs
    // Frozen phase → past-tense "Thought" pill, no shimmer. Live phase →
    // present-tense "Thinking" pill with shimmer + wall-clock counter.
    // Without this split, history pills (where the last event happens to be
    // a thinking message) shimmer forever and read "Thinking" — they're
    // not actually thinking anymore, the turn is done.
    if (!isLiveTurn) {
      return { kind: 'thinking-done', elapsedMs: durationMs ?? 0 }
    }
    const elapsedMs =
      startMs !== undefined ? Math.max(0, now - startMs) : 0
    return { kind: 'thinking', elapsedMs }
  }

  // ── 3. Frozen "finished turn" with a prior tool/thinking ──────────────
  // Spec §3 final paragraph: when the turn is Finished and there's a prior
  // tool, pass through the LAST tool's frozen state (no separate "finished"
  // branch on the pill). The lastNonStatus check above handles the case
  // when a tool is the most recent event; here we cover the case where an
  // assistantText followed a tool — we still want the tool's summary on
  // the pill, not "Working…".
  if (lastTool) {
    const formatted = formatTool(messageToToolEvent(lastTool))
    return frozenToolState(lastTool, formatted)
  }
  if (lastThinking) {
    // Edge case: thinking with no tool calls, but a later assistant bubble
    // (or some other non-thinking, non-tool event) made it no longer the
    // most recent live event. The model is definitely done thinking — freeze
    // on "Thought" with the canonical duration if present, otherwise 0.
    // We also use the frozen state for non-live turns even when thinking IS
    // the last event (handled in §2 above) — so anywhere we reach this
    // branch, the pill should be past-tense.
    return {
      kind: 'thinking-done',
      elapsedMs: lastThinking.thinkingDurationMs ?? 0,
    }
  }

  // ── 4. No tool / thinking yet — drive off the latest status ──────────
  if (lastStatus) {
    if (lastStatus.status === 'Creating') return { kind: 'creating' }
    if (lastStatus.status === 'Running') {
      const taskTitle = lastTask?.title ?? undefined
      return taskTitle ? { kind: 'running-idle', taskTitle } : { kind: 'running-idle' }
    }
    if (lastStatus.status === 'Finished') {
      // Finished with no tool/thinking ever — quiet "running-idle" frozen,
      // no shimmer. Falls through to running-idle, which the pill renders
      // with shimmer; that's fine for the active case but reads as nervous
      // for a finished-no-tool turn. We treat that case below by returning
      // a neutral "creating"-shaped state isn't right either — instead we
      // surface running-idle so the pill's frozen tone matches the spec's
      // "nothing more to say" frame.
      return { kind: 'running-idle' }
    }
  }

  // ── 5. No events at all — the turn just started. ──────────────────────
  return { kind: 'creating' }
}

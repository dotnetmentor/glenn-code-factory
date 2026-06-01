// Reconnect schedule for the daemon's `RuntimeHub` connection. The daemon must
// keep retrying *forever* — there is no "give up" branch because the daemon's
// only purpose is to be reachable by the main API. The default
// `DefaultReconnectPolicy` from @microsoft/signalr only retries 4 times then
// gives up, which is wrong for our use case.
//
// Schedule:
//   attempt 0 →     0 ms (immediate retry)
//   attempt 1 →  2000 ms
//   attempt 2 →  5000 ms
//   attempt 3 → 10000 ms
//   attempt 4 → 30000 ms
//   attempt 5+ → 30000 ms (steady-state)
//
// The first five values match SignalR's default schedule so transient blips
// behave identically to a vanilla client; the cap at 30 s after that keeps the
// reconnect noise reasonable while still being responsive when the server
// comes back.

import type { IRetryPolicy, RetryContext } from '@microsoft/signalr'

const SCHEDULE_MS: readonly number[] = [0, 2_000, 5_000, 10_000, 30_000]
const STEADY_STATE_MS = 30_000

export class IndefiniteReconnectPolicy implements IRetryPolicy {
  nextRetryDelayInMilliseconds(ctx: RetryContext): number | null {
    // Defensive: previousRetryCount can in theory be negative on a buggy
    // transport. Treat anything <0 as "first attempt".
    const idx = Math.max(0, ctx.previousRetryCount)
    if (idx < SCHEDULE_MS.length) {
      // `noUncheckedIndexedAccess` makes the lookup `number | undefined`. The
      // bounds check above guarantees defined; the `?? STEADY_STATE_MS` keeps
      // the type narrow without an `as` cast.
      return SCHEDULE_MS[idx] ?? STEADY_STATE_MS
    }
    return STEADY_STATE_MS
  }
}

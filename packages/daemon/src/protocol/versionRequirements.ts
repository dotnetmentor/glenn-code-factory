// Protocol-version handshake.
//
// As the daemon ↔ main API wire format grows, individual hub methods may pin a
// minimum daemon version below which they are not safe to dispatch (e.g. a
// schema field becomes mandatory in the daemon's reply). This module owns the
// authoritative table of those minima + a tiny helper the composition root
// uses to gate inbound handlers.
//
// v1 ships with a single illustrative entry (`ApplyRuntimeSpecDelta` at 0.4.0)
// and no live callsite — there is no `ApplyRuntimeSpecDelta` handler yet on
// the SignalRClient inbound side. The helper is wired in `main.ts` as a guard
// that future handlers can call. Adding new entries means: append to the table,
// call `requireProtocol(method, DAEMON_VERSION)` inside the new handler, emit a
// structured error event when it returns `ok: false`.
//
// The helper is permissive about non-semver daemon versions: `semver.coerce`
// turns 'dev', '0.1.0-dev', '1.2', etc. into a real semver string, defaulting
// to '0.0.0' on completely garbled input. That keeps a malformed
// DAEMON_VERSION from silently bypassing every gate (it would only ever pass
// version-checks against required: '0.0.0').

import semver from 'semver'

/**
 * Minimum daemon version required to dispatch each gated hub method. Methods
 * absent from this map have no version requirement and pass unconditionally.
 *
 * Add entries when a wire-format change ships that pre-existing daemons cannot
 * round-trip safely.
 */
export const MIN_PROTOCOL_VERSIONS: Record<string, string> = {
  // Example — there is no inbound handler for this method yet on SignalRClient.
  // The entry exists so the helper has something concrete to gate; the first
  // production handler that lands will reuse this pattern.
  ApplyRuntimeSpecDelta: '0.4.0',
}

export type ProtocolCheck =
  | { ok: true }
  | { ok: false; required: string; current: string }

/**
 * Return whether the running daemon meets the minimum protocol version for
 * `method`. Unknown methods always pass. Non-semver `daemonVersion` strings
 * are coerced via `semver.coerce`; completely garbled input becomes '0.0.0'
 * so it fails closed against any non-zero requirement.
 */
export function requireProtocol(method: string, daemonVersion: string): ProtocolCheck {
  const required = MIN_PROTOCOL_VERSIONS[method]
  if (required === undefined) return { ok: true }
  const cleanCurrent = semver.coerce(daemonVersion)?.version ?? '0.0.0'
  if (semver.gte(cleanCurrent, required)) return { ok: true }
  return { ok: false, required, current: cleanCurrent }
}

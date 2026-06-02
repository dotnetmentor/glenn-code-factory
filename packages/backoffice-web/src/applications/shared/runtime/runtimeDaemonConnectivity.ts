import { RuntimeState, type RuntimeStatusResponse } from '@/api/queries-commands'

/** Empty-state caption when the runtime daemon is not on RuntimeHub yet. */
export const DAEMON_LOGS_UNAVAILABLE_MESSAGE =
  'Daemon not connected. Logs appear once RuntimeHub connects.'

/** States where the daemon is on RuntimeHub but heartbeats have not started yet. */
const MID_BOOT_HUB_CONNECTED: ReadonlySet<RuntimeState> = new Set([
  RuntimeState.Booting,
  RuntimeState.Bootstrapping,
  RuntimeState.Waking,
])

/** True when the daemon has connected to RuntimeHub but not sent its first heartbeat. */
export function isDaemonMidBootConnected(
  status: RuntimeStatusResponse | undefined,
): boolean {
  if (!status?.state) return false
  return MID_BOOT_HUB_CONNECTED.has(status.state)
}

/**
 * True when the daemon has never heartbeated (or status is unknown). In that
 * state {@code StartDaemonLogTail} cannot reach the machine and log lines will
 * not arrive even though {@code SubscribeToDaemonLogs} succeeds.
 *
 * During Booting/Bootstrapping/Waking the daemon connects to RuntimeHub early
 * and streams bootstrap events, but {@code HeartbeatModule.start()} only runs
 * after bootstrap completes — so {@code lastHeartbeatAt} stays null until Online.
 */
export function isDaemonHubLikelyUnreachable(
  status: RuntimeStatusResponse | undefined,
): boolean {
  if (!status) return true
  if (status.lastHeartbeatAt) return false
  return !isDaemonMidBootConnected(status)
}

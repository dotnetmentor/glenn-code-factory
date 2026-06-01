import { SysstatsView } from '@/applications/shared/runtime/components/SysstatsView'
import type { HeartbeatSnapshot } from '../hooks/useRuntimeEventStream'

export interface SysstatsPanelProps {
  /**
   * Latest heartbeat snapshot derived from the polled runtime/status row.
   * Null while no heartbeat has landed; in that case the accordion renders a
   * subtle waiting state instead of empty cards so the operator can tell the
   * difference between "drawer just opened" and "daemon never reported".
   */
  heartbeatSnapshot: HeartbeatSnapshot | null
}

/**
 * Collapsible sysstats panel mounted at the top of the {@link RuntimeDrawer}.
 * Thin wrapper around the shared {@link SysstatsView} — preserves the
 * super-admin drawer's accordion behavior by rendering with
 * {@code embedded={false}}.
 *
 * <p>The real body lives in
 * {@code applications/shared/runtime/components/SysstatsView.tsx} so the
 * workspace runtime panel can mount the same surface bare ({@code
 * embedded={true}}).</p>
 */
export function SysstatsPanel({ heartbeatSnapshot }: SysstatsPanelProps) {
  return <SysstatsView heartbeatSnapshot={heartbeatSnapshot} embedded={false} />
}

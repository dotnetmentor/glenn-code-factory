/**
 * Human-readable byte formatting shared across the super-admin runtime
 * observability surfaces.
 *
 * <p>Picks the largest unit that keeps the displayed value in the [1, 1024)
 * range and renders one decimal for values < 10, otherwise rounds to the
 * nearest integer. Negative or non-finite input renders as an em-dash so
 * "no value yet" cells in a table don't display garbage.</p>
 *
 * <p>Units are binary (KB = 1024 bytes), matching how supervisord, /proc, and
 * /sys/class/net/eth0/statistics report counters. We keep the conventional
 * "KB / MB / GB / TB" suffix rather than the formally-correct "KiB / MiB / …"
 * because every other surface in the app already uses the binary suffix and
 * mixing styles is worse than the millibel discrepancy.</p>
 */
export function formatBytes(bytes: number | null | undefined): string {
  if (bytes == null || !Number.isFinite(bytes) || bytes < 0) return '—'
  if (bytes < 1024) return `${Math.round(bytes)} B`
  const units = ['KB', 'MB', 'GB', 'TB', 'PB']
  let value = bytes / 1024
  let i = 0
  while (value >= 1024 && i < units.length - 1) {
    value /= 1024
    i++
  }
  return `${value < 10 ? value.toFixed(1) : Math.round(value)} ${units[i]}`
}

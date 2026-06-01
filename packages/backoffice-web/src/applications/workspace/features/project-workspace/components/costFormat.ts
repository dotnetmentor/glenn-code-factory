// ── Cost / token formatters ─────────────────────────────────────────────────
//
// Tiny pure helpers shared by every surface that renders a dollar amount or a
// token count — per-turn badge, branch header total, project nav rollup,
// workspace total. Kept in one place so the four call sites stay byte-for-byte
// consistent (and so a future "show fractional cents differently" tweak lands
// once).
//
// Format rules:
//   * Costs under one cent get four decimals ("$0.0042"). Anything from one
//     cent up gets the familiar two-decimal price ("$1.23"). This avoids the
//     ambiguous "$0.00" we'd otherwise see on cheap turns where the dollar
//     amount really is non-zero, just sub-cent.
//   * Token counts use the ambient locale's thousand separators via
//     {@link Intl.NumberFormat}. We instantiate a single formatter at module
//     load (cheap) rather than per-call so hot paths don't reallocate.
//
// Both functions defensively coerce {@code null}/{@code undefined} to {@code 0}
// so callers that just spread fields off a {@code SessionSummary} (where the
// new cost fields are nullable on legacy rows) don't have to litter the call
// site with {@code ?? 0}.

const TOKENS_FORMATTER = new Intl.NumberFormat()

/**
 * Format a USD cost for display in any of the ambient cost surfaces.
 *
 * <p>Returns "$0.0042" for sub-cent amounts (four decimals) and "$1.23" for
 * anything from one cent up (two decimals). Negative amounts shouldn't occur
 * in practice but are formatted with the same rule for visual stability.</p>
 */
export function formatCostUsd(costUsd: number | null | undefined): string {
  const v = costUsd ?? 0
  const abs = Math.abs(v)
  // Use 4 decimals for sub-cent values so we don't collapse a real, non-zero
  // cost into a misleading "$0.00". Above the penny boundary the regular
  // two-decimal price reads more cleanly.
  if (abs > 0 && abs < 0.01) {
    return `$${v.toFixed(4)}`
  }
  return `$${v.toFixed(2)}`
}

/**
 * Format a token count with locale-aware thousand separators.
 *
 * <p>Used in the per-turn badge's tooltip breakdown rows. Coerces null/undef to
 * zero so callers can pass nullable fields straight off the wire.</p>
 */
export function formatTokens(n: number | null | undefined): string {
  return TOKENS_FORMATTER.format(n ?? 0)
}

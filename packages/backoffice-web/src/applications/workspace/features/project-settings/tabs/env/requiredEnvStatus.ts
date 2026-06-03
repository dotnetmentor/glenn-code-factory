import type {
  EnvStatusResponse,
  RequiredEnvStatusItem,
} from '../../../../../../api/queries-commands'

/**
 * Derive the list of required env vars that are NOT satisfied on a branch from
 * the branch env status. A key can surface two ways: as an unsatisfied entry in
 * {@code required} (carries its declaring service), or as a bare key in
 * {@code missing} with no matching required descriptor (mapped to an "Unknown"
 * service). We merge both, de-duplicating on key.
 *
 * <p>Shared between the Environment tab (which renders a row per item) and the
 * project-settings drawer nav (which badges the count) so the two never drift.</p>
 */
export function missingRequiredEnvItems(
  status: EnvStatusResponse | undefined,
): RequiredEnvStatusItem[] {
  if (!status) return []
  const missingSet = new Set(status.missing)
  const fromRequired = status.required.filter(
    (r) => !r.satisfied && missingSet.has(r.key),
  )
  const covered = new Set(fromRequired.map((r) => r.key))
  const bareMissing: RequiredEnvStatusItem[] = status.missing
    .filter((k) => !covered.has(k))
    .map((k) => ({ service: 'Unknown', key: k, satisfied: false }))
  return [...fromRequired, ...bareMissing]
}

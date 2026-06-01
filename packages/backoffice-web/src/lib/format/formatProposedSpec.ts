import type { RuntimeSpecV3 } from '@/api/queries-commands'

function sortKeysReplacer(_key: string, value: unknown): unknown {
  if (
    value &&
    typeof value === 'object' &&
    !Array.isArray(value) &&
    value.constructor === Object
  ) {
    const obj = value as Record<string, unknown>
    return Object.keys(obj)
      .sort()
      .reduce<Record<string, unknown>>((acc, k) => {
        acc[k] = obj[k]
        return acc
      }, {})
  }
  return value
}

/** Pretty-print a RuntimeSpecV3 object with stable key order for diff display. */
export function formatRuntimeSpec(spec: RuntimeSpecV3): string {
  return JSON.stringify(spec, sortKeysReplacer, 2)
}

/** Pretty-print a proposed RuntimeSpecV3 JSON string for diff display. */
export function formatProposedSpec(raw: string): string {
  if (!raw) return ''
  try {
    const parsed = JSON.parse(raw) as RuntimeSpecV3
    return formatRuntimeSpec(parsed)
  } catch {
    return raw
  }
}

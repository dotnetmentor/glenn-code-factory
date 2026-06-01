import type { RuntimeSpecV3, ServiceInstance } from '@/api/queries-commands'
import { formatRuntimeSpec } from '@/lib/format/formatProposedSpec'

export interface ServiceFieldErrors {
  name?: string
  kind?: string
}

export interface SpecValidation {
  /** True when the spec passes all validation rules. */
  isValid: boolean
  /** Per-service error messages keyed by index. */
  serviceErrors: Record<number, ServiceFieldErrors>
  /** Flat list of human-readable errors (banner-friendly). */
  errorMessages: string[]
}

/**
 * Validate a {@link RuntimeSpecV3} against the rules the editor surface
 * enforces before allowing a "Propose changes" submission:
 *
 * <ul>
 *   <li>Top-level {@code version} must be 3.</li>
 *   <li>Every service has a non-empty {@code kind} (preset slug).</li>
 *   <li>Every service has a non-empty {@code name}.</li>
 *   <li>No two services share a {@code name} (case-insensitive).</li>
 *   <li>Per-service {@code values} (if present) must be an object.</li>
 * </ul>
 *
 * <p>The expander (server-side) is the source of truth for "is this preset
 * kind known and are all required parameter values present" — we don't
 * duplicate the preset registry here.</p>
 */
export function validateSpec(spec: RuntimeSpecV3): SpecValidation {
  const serviceErrors: Record<number, ServiceFieldErrors> = {}
  const errorMessages: string[] = []

  if (spec.version !== 3) {
    errorMessages.push(
      `Spec version must be 3 (got ${spec.version ?? 'undefined'}).`,
    )
  }

  const services = spec.services ?? []
  const seenNames = new Map<string, number>()

  services.forEach((service: ServiceInstance, index: number) => {
    const errors: ServiceFieldErrors = {}
    const trimmedName = (service.name ?? '').trim()
    const trimmedKind = (service.kind ?? '').trim()

    if (trimmedKind.length === 0) {
      errors.kind = 'Preset (kind) is required.'
    }

    if (trimmedName.length === 0) {
      errors.name = 'Name is required.'
    } else {
      const key = trimmedName.toLowerCase()
      const prior = seenNames.get(key)
      if (prior != null) {
        errors.name = `Duplicate name (also used by service #${prior + 1}).`
        if (!serviceErrors[prior]?.name) {
          serviceErrors[prior] = {
            ...(serviceErrors[prior] ?? {}),
            name: `Duplicate name (also used by service #${index + 1}).`,
          }
        }
      } else {
        seenNames.set(key, index)
      }
    }

    if (
      service.values != null &&
      (typeof service.values !== 'object' || Array.isArray(service.values))
    ) {
      errors.kind =
        (errors.kind ? `${errors.kind} ` : '') +
        'Service "values" must be a JSON object.'
    }

    if (errors.name || errors.kind) {
      serviceErrors[index] = { ...(serviceErrors[index] ?? {}), ...errors }
    }
  })

  Object.entries(serviceErrors).forEach(([idx, errs]) => {
    if (errs.kind) errorMessages.push(`Service #${Number(idx) + 1}: ${errs.kind}`)
    if (errs.name)
      errorMessages.push(`Service #${Number(idx) + 1}: ${errs.name}`)
  })

  return {
    isValid: errorMessages.length === 0,
    serviceErrors,
    errorMessages,
  }
}

/**
 * Canonical JSON serialization for diff/equality checks. Sorts object keys
 * so the diff between two semantically identical specs is empty even when
 * key order changed.
 */
export function canonicalizeSpec(spec: RuntimeSpecV3): string {
  return formatRuntimeSpec(spec)
}

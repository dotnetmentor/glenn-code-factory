import type { ServiceSpec } from '../runtime/SupervisordController.js'

export interface RequiredEnvDecl {
  key: string
  description?: string
  secret?: boolean
  /** When false, listed in UI but does not block service start. Default true. */
  required?: boolean
}

/**
 * Pull the (possibly absent) `requiredEnv` declarations off a service spec.
 * Returns `[]` when the field is missing or malformed.
 */
export function readRequiredEnv(spec: unknown): RequiredEnvDecl[] {
  const raw = (spec as { requiredEnv?: unknown }).requiredEnv
  if (!Array.isArray(raw)) return []
  return raw.filter(
    (e): e is RequiredEnvDecl =>
      typeof e === 'object' &&
      e !== null &&
      typeof (e as { key?: unknown }).key === 'string',
  )
}

function isBlockingRequired(decl: RequiredEnvDecl): boolean {
  return decl.required !== false
}

/**
 * Merge declared env keys from the runtime snapshot into a service spec's
 * supervisord `environment=` block when values are present. Preset `spec.env`
 * values win on key collision.
 */
export function mergeServiceRuntimeEnv(
  spec: ServiceSpec,
  runtimeEnv: ReadonlyMap<string, string>,
  declarations: RequiredEnvDecl[] = readRequiredEnv(spec),
): ServiceSpec {
  const merged: Record<string, string> =
    spec.env != null ? { ...spec.env } : {}

  for (const { key } of declarations) {
    const value = runtimeEnv.get(key)
    if (value !== undefined && value !== '') {
      merged[key] = value
    }
  }

  if (Object.keys(merged).length === 0) {
    return spec
  }
  return { ...spec, env: merged }
}

export function missingRequiredEnvKeys(
  spec: unknown,
  runtimeEnv: ReadonlyMap<string, string>,
): string[] {
  return readRequiredEnv(spec)
    .filter(isBlockingRequired)
    .map((r) => r.key)
    .filter((key) => {
      const value = runtimeEnv.get(key)
      return value === undefined || value === ''
    })
}

export function allRequiredEnvPresent(
  spec: unknown,
  runtimeEnv: ReadonlyMap<string, string>,
): boolean {
  return missingRequiredEnvKeys(spec, runtimeEnv).length === 0
}

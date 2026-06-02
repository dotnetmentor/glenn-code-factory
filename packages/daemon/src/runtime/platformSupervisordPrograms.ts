/**
 * Supervisord programs managed by the daemon outside `runtimeSpec.services`.
 * These must never be torn down during spec-service reconciliation.
 */
export const PLATFORM_MANAGED_SUPERVISORD_PROGRAMS = ['cloudflared'] as const

export type PlatformManagedSupervisordProgram =
  (typeof PLATFORM_MANAGED_SUPERVISORD_PROGRAMS)[number]

export function isPlatformManagedSupervisordProgram(name: string): boolean {
  return (PLATFORM_MANAGED_SUPERVISORD_PROGRAMS as readonly string[]).includes(name)
}

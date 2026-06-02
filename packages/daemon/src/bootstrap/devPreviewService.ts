import type { ServiceSpec } from '../runtime/SupervisordController.js'

/**
 * True for Vite/npm dev-server programs whose optimize-deps cache goes stale
 * when `npm ci` runs in setup while the process stays up on a warm volume.
 */
export function isDevPreviewService(spec: Pick<ServiceSpec, 'command'>): boolean {
  const cmd = spec.command.toLowerCase()
  return (
    cmd.includes('npm run dev') ||
    cmd.includes('npx vite') ||
    /\bvite\b/.test(cmd)
  )
}

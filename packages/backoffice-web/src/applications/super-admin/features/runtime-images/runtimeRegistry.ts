/**
 * Container registry path the runtime base image is published under — e.g.
 * `registry.fly.io/runtime-base`.
 *
 * Configurable at build time via the `VITE_RUNTIME_IMAGE_REGISTRY` env var so
 * self-hosters can point the Super Admin → Runtime Images surface at their own
 * Fly app without editing source. Falls back to a generic default that matches
 * the publish scripts' default (`scripts/publish-runtime-image*.sh`).
 */
export const RUNTIME_IMAGE_REGISTRY: string =
  import.meta.env.VITE_RUNTIME_IMAGE_REGISTRY ?? 'registry.fly.io/glenn-runtime-base'

/**
 * Image-name segment of {@link RUNTIME_IMAGE_REGISTRY} (the part after the
 * registry host) — e.g. `glenn-runtime-base`. Passed to the registry-tags API
 * so the "Available in Fly registry" list matches the configured registry
 * instead of the backend default. Keep `VITE_RUNTIME_IMAGE_REGISTRY` in sync
 * with the backend `RuntimeImages:DefaultImageName` setting.
 */
export const RUNTIME_IMAGE_NAME: string =
  RUNTIME_IMAGE_REGISTRY.split('/').pop() || 'glenn-runtime-base'

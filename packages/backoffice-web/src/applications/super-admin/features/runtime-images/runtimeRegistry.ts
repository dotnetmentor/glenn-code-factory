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
  import.meta.env.VITE_RUNTIME_IMAGE_REGISTRY ?? 'registry.fly.io/runtime-base'

/** Super Admin route for provisioning preview subdomains. */
export const SUBDOMAINS_ADMIN_PATH = '/super-admin/subdomains'

/**
 * Backend signals an exhausted preview-subdomain pool with HTTP 409 and
 * body `{ "error": "pool_empty" }` (create project, copy branch, etc.).
 */
export function isPoolEmptyError(err: unknown): boolean {
  const maybe = err as
    | { response?: { status?: number; data?: { error?: string } } }
    | undefined
  return (
    maybe?.response?.status === 409 && maybe?.response?.data?.error === 'pool_empty'
  )
}

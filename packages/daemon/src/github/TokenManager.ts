// TokenManager — in-memory cache + lazy refresh of GitHub App installation
// tokens that the daemon uses to authenticate HTTPS clones / fetches.
//
// === Why this lives in the daemon ===
//
// The .NET API mints installation tokens via the
// `IRuntimeHub.GetRepoAccessToken(repoFullName)` hub method. Tokens are
// short-lived (≤ 1 hour) and rate-limited (5000 / hour / installation), so the
// daemon must:
//
//   1. Cache the token until it's actually expired (or near-expired).
//   2. De-duplicate concurrent refreshes — two parallel clones for the same
//      repo must not double-mint.
//   3. Force-refresh after a 401/403 so a compromised token doesn't loop.
//
// === Cache key ===
//
// We key by `repoFullName` alone, NOT by `(runtimeId, repoFullName)`. The
// daemon is one-runtime-per-process — the runtimeId is implicit in the
// SignalR connection's claims; the hub side stamps it. Keying on it inside
// the cache would buy nothing and just add a parameter to every callsite.
//
// === Lazy refresh with a 5-minute buffer ===
//
// We refresh when the cached token is within 5 minutes of its
// `expiresAt`. Five minutes is comfortably larger than any single git clone
// we expect to run and well within the 1-hour token lifetime; it gives us
// ample headroom against clock skew between the daemon's wall clock and the
// hub's.
//
// === No persistence ===
//
// Tokens never touch disk. A daemon restart re-mints — fast, simple, and
// avoids any "stale token on disk after rotation" failure modes.

import type { Logger } from 'pino'

/**
 * Public surface used by clone / fetch callers.
 */
export interface TokenManager {
  /**
   * Returns a fresh installation token for `repoFullName`. Refreshes from the
   * hub if the cache is cold, the cached token is past or near expiry, or
   * `opts.forceRefresh` is true. Concurrent calls for the same repo share a
   * single in-flight fetch (single-flight dedup).
   *
   * Propagates errors from the hub call — callers (CloningRepoStage) surface
   * them as recoverable stage failures.
   */
  getToken(repoFullName: string, opts?: { forceRefresh?: boolean }): Promise<string>

  /**
   * Drop any cached token for `repoFullName`. Used by the 401 retry path in
   * CloningRepoStage: a stale/revoked token in cache must not be served
   * again before the next forced refresh.
   */
  invalidate(repoFullName: string): void
}

/**
 * Minimal slice of SignalRClient the token manager needs. Keeps tests free of
 * the full client — they pass a `{ getRepoAccessToken: async () => ... }` stub.
 *
 * `expiresAt` is typed loosely (`string | Date`) because the generated
 * Tapper DTO straddles both — wire JSON is a string, but a consumer that
 * has already parsed dates upstream might hand us a `Date`. Normalising
 * here means callers don't have to.
 */
export interface TokenManagerDeps {
  signalr: {
    getRepoAccessToken(repoFullName: string): Promise<{ token: string; expiresAt: string | Date }>
  }
  logger: Logger
  /**
   * Test seam — default `() => new Date()`. Tests inject a deterministic
   * clock so cache-hit / near-expiry / force-refresh paths can be exercised
   * without setTimeout or vi.useFakeTimers gymnastics.
   */
  now?: () => Date
}

/**
 * Refresh when the cached token has less than this much life left.
 */
const REFRESH_BUFFER_MS = 5 * 60_000

interface CacheEntry {
  token: string
  expiresAt: Date
}

export function createTokenManager(deps: TokenManagerDeps): TokenManager {
  const logger = deps.logger.child({ module: 'github-token-manager' })
  const now = deps.now ?? (() => new Date())

  // Cache and inflight maps are keyed solely by repoFullName — see the
  // top-of-file note about why runtimeId isn't part of the key.
  const cache = new Map<string, CacheEntry>()
  const inflight = new Map<string, Promise<string>>()

  function isFreshEnough(entry: CacheEntry): boolean {
    return entry.expiresAt.getTime() - now().getTime() >= REFRESH_BUFFER_MS
  }

  async function refresh(repoFullName: string, reason: string): Promise<string> {
    // Surface only the reason + repo; NEVER the token (a previous cached
    // token, or the one about to be received).
    logger.info({ repoFullName, reason }, 'fetching GitHub installation token')
    try {
      const result = await deps.signalr.getRepoAccessToken(repoFullName)
      const expiresAt =
        result.expiresAt instanceof Date ? result.expiresAt : new Date(result.expiresAt)
      cache.set(repoFullName, { token: result.token, expiresAt })
      logger.info(
        { repoFullName, expiresAt: expiresAt.toISOString() },
        'GitHub installation token refreshed',
      )
      return result.token
    } catch (err) {
      // Do not poison the cache — leave whatever was there (if anything) so
      // a subsequent call gets a clean shot at a refresh. Propagate so the
      // caller can decide (e.g. emit a stage failure).
      logger.warn({ err, repoFullName }, 'GitHub installation token refresh failed')
      throw err
    }
  }

  function startRefresh(repoFullName: string, reason: string): Promise<string> {
    // Single-flight: if a refresh is already in flight for this repo, every
    // concurrent caller awaits the same promise. We clean up the inflight
    // entry on both resolve and reject so a transient failure doesn't poison
    // future calls (next caller observes no inflight, kicks a fresh refresh).
    const existing = inflight.get(repoFullName)
    if (existing !== undefined) return existing

    const p = refresh(repoFullName, reason).finally(() => {
      inflight.delete(repoFullName)
    })
    inflight.set(repoFullName, p)
    return p
  }

  return {
    async getToken(repoFullName, opts): Promise<string> {
      if (opts?.forceRefresh === true) {
        return startRefresh(repoFullName, 'forced')
      }

      const cached = cache.get(repoFullName)
      if (cached === undefined) {
        return startRefresh(repoFullName, 'cold cache')
      }
      if (!isFreshEnough(cached)) {
        return startRefresh(repoFullName, 'near expiry')
      }
      return cached.token
    },

    invalidate(repoFullName): void {
      // Drop both the cached token and any in-flight refresh promise. The
      // in-flight drop matters when the 401 retry path calls invalidate
      // mid-fetch: the original fetch may still resolve into the cache, but
      // the retry's forceRefresh will be a fresh single-flight that won't
      // share the previous (potentially-stale) promise.
      cache.delete(repoFullName)
      inflight.delete(repoFullName)
    },
  }
}

// secretRedactor.ts — last-line-of-defence scrubber for secret-shaped strings
// in arbitrary log text. Sibling to the daemon's existing pino setup; operates
// at the streamWrite hook so every emitted line passes through it regardless
// of which call-site logged it.
//
// === Parity with the backend ===
//
// The .NET API has a mirror-image scrubber at
// packages/dotnet-api/Source/Infrastructure/Logging/SecretValueRedactor.cs.
// The two halves carry the SAME regex set (same names, same patterns, same
// default-enabled flags) so coverage stays in lockstep across runtimes. When
// you change a regex here, change the C# side in the same commit. The reason
// is defence-in-depth: a leaked project secret can flow through either
// runtime's logs, and we want one reviewer to be able to compare both files
// diff-by-diff.
//
// === Why these patterns ===
//
// Project-secrets (Spec 14) lets users store API keys, OAuth tokens, etc. as
// per-project env vars that the daemon injects into hook subprocesses. Even
// with disciplined logging upstream (we redact known property names already),
// a secret can sneak in via verbose subprocess stderr, exception messages,
// or third-party libs that log request bodies. The regexes below match the
// well-known formats issued by major providers; GENERIC_HIGH_ENTROPY would
// catch the long tail but is disabled by default — see comment there.
//
// === Performance ===
//
// `redactSecrets` is on the per-log-line hot path. We compile every regex at
// module load and short-circuit early if the input doesn't contain any of the
// trigger substrings. Per-call cost on a no-match line is one indexOf scan
// over a small set of literals — well under the 50µs budget the spec sets.

import type { Logger, LoggerOptions } from 'pino'

export const REDACTED = '[REDACTED]'

// ---------------------------------------------------------------------------
// Regex set — keep in sync with packages/dotnet-api/Source/Infrastructure/
// Logging/SecretValueRedactor.cs
// ---------------------------------------------------------------------------
//
// Each pattern is named for its issuer/format. Word-boundary anchors (\b)
// bracket each pattern so we don't smear into surrounding text in structured
// log lines like `{"key":"sk_live_..."}`. All compiled once at module load
// with the global flag for repeated replacement within one input.

/** Stripe secret keys: `sk_test_…` / `sk_live_…` / `sk_…`. */
export const STRIPE_SECRET_LIKE = /\bsk_(test_|live_)?[A-Za-z0-9]{20,}\b/g

/**
 * Stripe publishable keys: `pk_test_…` / `pk_live_…` / `pk_…`.
 * Lower risk than secret keys (publishable by definition) but still
 * PII-grade in some flows; cheap to redact.
 */
export const STRIPE_PUBLIC_LIKE = /\bpk_(test_|live_)?[A-Za-z0-9]{20,}\b/g

/**
 * HTTP Bearer-token headers. Case-insensitive on the literal "Bearer"
 * prefix because some clients emit lowercase. The whole "Bearer X" run is
 * replaced — mirroring how the backend's SecretValueRedactor handles it,
 * and how JwtRedactor (in the same redaction chain) swallows the entire
 * token. The token itself might separately match a JWT pattern; if so the
 * JWT scrub wins and this regex finds nothing.
 */
export const BEARER_TOKEN = /\bBearer\s+[A-Za-z0-9._-]{20,}\b/gi

/**
 * AWS access key IDs: `AKIA` + 16 uppercase alphanumerics. AWS publishes
 * this exact shape; matches deterministically.
 */
export const AWS_ACCESS_KEY = /\bAKIA[0-9A-Z]{16}\b/g

/**
 * OpenAI keys: `sk-…` or `sk-proj-…`. Note the dash — distinct from
 * Stripe's underscore-separated `sk_…`, so the two patterns don't collide.
 */
export const OPENAI_KEY = /\bsk-(proj-)?[A-Za-z0-9_-]{20,}\b/g

/**
 * Generic high-entropy strings of length >= 40. This catches long
 * base64/hex secrets that don't match any provider-specific shape
 * (Postgres URLs with embedded passwords, generic API keys, etc.).
 *
 * DISABLED BY DEFAULT: real-world false-positive rate is too high — UUIDs
 * concatenated with slashes (Git refs, S3 paths), file content hashes,
 * log correlation IDs, and base64-encoded telemetry blobs all hit this.
 * Enabling it scrubs huge swaths of legitimate logs and makes triage
 * harder than the leak it's meant to catch.
 *
 * Kept defined so callers can opt in via the `includeHighEntropy` flag for
 * environments where the calculus differs (e.g. customer reports a leak
 * via this exact shape and we need the backstop NOW).
 */
export const GENERIC_HIGH_ENTROPY = /\b[A-Za-z0-9+/=_-]{40,}\b/g

const DEFAULT_PATTERNS: readonly RegExp[] = [
  STRIPE_SECRET_LIKE,
  STRIPE_PUBLIC_LIKE,
  BEARER_TOKEN,
  AWS_ACCESS_KEY,
  OPENAI_KEY,
]

const ALL_PATTERNS_INCLUDING_HIGH_ENTROPY: readonly RegExp[] = [
  ...DEFAULT_PATTERNS,
  GENERIC_HIGH_ENTROPY,
]

// Cheap-trigger substrings — if NONE of these appear in the input, none of
// the regexes can possibly match. This keeps the hot path under the 50µs
// budget for normal log lines (which never contain a secret). Lowercased
// indexOf checks would be even cheaper, but `Bearer` matters case-sensitively
// only for the pure string scan — the regex itself is case-insensitive so we
// fold the test side too via the `bearer` substring covering both casings
// when the source is lowercased once.
const TRIGGER_SUBSTRINGS_LOWER: readonly string[] = [
  'sk_',
  'pk_',
  'sk-',
  'bearer',
  'akia',
]

/**
 * Returns `input` with every secret-shaped substring replaced by
 * `[REDACTED]`. Default pattern set excludes `GENERIC_HIGH_ENTROPY`; pass
 * `includeHighEntropy: true` to opt in.
 *
 * Empty/non-string input is returned as-is (or as empty string for null/
 * undefined) so callers can rely on a string result.
 */
export function redactSecrets(
  input: string,
  options: { includeHighEntropy?: boolean } = {},
): string {
  if (typeof input !== 'string' || input.length === 0) {
    return input ?? ''
  }
  // Fast-path: scan once for any trigger substring. The check is
  // case-insensitive via toLowerCase on the input; allocating one lowercased
  // copy is cheaper than running every regex against the original. The
  // lowercased copy is discarded — we replace against the original.
  const probe = input.toLowerCase()
  let mayMatch = false
  for (const trigger of TRIGGER_SUBSTRINGS_LOWER) {
    if (probe.includes(trigger)) {
      mayMatch = true
      break
    }
  }
  // GENERIC_HIGH_ENTROPY has no cheap trigger (any 40+ alnum run), so if the
  // caller opted in we skip the fast-path entirely.
  if (!mayMatch && !options.includeHighEntropy) {
    return input
  }

  const patterns = options.includeHighEntropy === true
    ? ALL_PATTERNS_INCLUDING_HIGH_ENTROPY
    : DEFAULT_PATTERNS
  let out = input
  for (const pattern of patterns) {
    // Reset lastIndex on each shared global regex — pino calls this per log
    // line and we don't want carry-over between calls.
    pattern.lastIndex = 0
    out = out.replace(pattern, REDACTED)
  }
  return out
}

/**
 * Returns true if `input` contains at least one secret-shaped substring under
 * the default pattern set. Useful for tests and for cheap pre-checks.
 */
export function containsSecretShape(input: string): boolean {
  if (typeof input !== 'string' || input.length === 0) return false
  const probe = input.toLowerCase()
  let mayMatch = false
  for (const trigger of TRIGGER_SUBSTRINGS_LOWER) {
    if (probe.includes(trigger)) {
      mayMatch = true
      break
    }
  }
  if (!mayMatch) return false
  for (const pattern of DEFAULT_PATTERNS) {
    pattern.lastIndex = 0
    if (pattern.test(input)) {
      return true
    }
  }
  return false
}

// ---------------------------------------------------------------------------
// Pino integration
// ---------------------------------------------------------------------------
//
// Pino has two redaction mechanisms: (1) `redact: { paths }` for known
// structured property paths — useful but requires every secret-bearing path
// to be enumerated up-front, which fails for stderr-tail strings, error
// .messages, and subprocess output that we can't predict. (2) `hooks` for
// content-based custom transforms.
//
// We use the `streamWrite` hook because it operates on the FINAL stringified
// JSON line just before it hits the destination stream. That means we get to
// scrub message text AND every property value (including nested ones) in one
// pass without having to walk the structured args ourselves. The trade-off
// is that the regex runs over the entire JSON payload, but the fast-path
// trigger scan keeps no-match cost minimal.
//
// Why not `logMethod`? It runs at log-call time, before pino has serialised
// the args, so we'd have to walk the (potentially deep) object ourselves. The
// streamWrite path lets us treat each log line as one flat string and is far
// simpler to reason about for this redaction backstop.

/**
 * Pino options fragment that wires `redactSecrets` into the streamWrite hook.
 * Merge this into your `pino()` options to enable the backstop.
 *
 * @example
 *   const logger = pino({ level: 'info', ...pinoSecretRedactionOptions() })
 */
export function pinoSecretRedactionOptions(
  options: { includeHighEntropy?: boolean } = {},
): LoggerOptions {
  return {
    hooks: {
      streamWrite: (s: string): string => redactSecrets(s, options),
    },
  }
}

/**
 * Convenience for callers that already have a partial pino options object:
 * merges `pinoSecretRedactionOptions()` into it, preserving any other hooks
 * the caller already declared (we wrap their streamWrite if they had one).
 */
export function withSecretRedaction(
  base: LoggerOptions,
  options: { includeHighEntropy?: boolean } = {},
): LoggerOptions {
  const existing = base.hooks?.streamWrite
  return {
    ...base,
    hooks: {
      ...base.hooks,
      streamWrite: (s: string): string => {
        const after = redactSecrets(s, options)
        return existing !== undefined ? existing(after) : after
      },
    },
  }
}

/**
 * Type guard / shape assertion for callers that want to make sure they got a
 * pino logger back. Not strictly necessary — `Logger` from 'pino' is
 * structural — but keeps the public surface small.
 */
export type RedactingLogger = Logger

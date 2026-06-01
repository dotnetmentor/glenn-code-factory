// BootstrapEnvironment — single source of truth for the shell environment used
// by every "we exec a snippet from the runtime spec" code path: `InstallStage`,
// `RunningSetupStage`, `RuntimeSpecApplier`, and the `dry_run_install` custom
// tool that lets the agent test snippets before proposing them.
//
// === Why this module exists ===
//
// Until 2026-05-28 each of those four call sites carried its own `DEFAULT_PATH`
// constant, copy-pasted across files. A typo (`/data/.mise/shims` with a stray
// leading dot) had been duplicated into all of them — plus the matching test
// assertions — so the tests "passed" while the bootstrap shipped a PATH that
// pointed at a directory mise never creates. Centralizing the constants here
// makes that whole class of drift impossible: tests assert against this module,
// runtime call sites consume this module, and the dry-run tool exposed to the
// agent uses the same values, so "works at proposal time" and "works at boot
// time" are guaranteed to use the same PATH.
//
// === What goes in PATH ===
//
//   - `/data/mise/shims`         — wrappers for tools installed via
//                                   `mise install X@Y`. Populated only after
//                                   the install stage runs at least once;
//                                   harmless when empty.
//   - `/usr/local/sbin`, `/usr/local/bin` — including `/usr/local/bin/mise`
//                                   itself (baked into the runtime base image
//                                   at Dockerfile.runtime-base layer 4). This
//                                   is why `mise install dotnet@9` works even
//                                   on a cold boot when `/data/mise/shims/` is
//                                   empty.
//   - `/usr/sbin`, `/usr/bin`, `/sbin`, `/bin` — standard system binaries
//                                   (bash, coreutils, apt-get, sudo, …).
//
// === Why HOME ===
//
// supervisord starts the daemon (and any child snippets the daemon spawns)
// without inheriting an interactive shell's HOME. Some tooling (npm, `gh`,
// mise's own self-update path) reads HOME to find user config or write
// caches. Pinning it to `/home/agent` matches the runtime image's `agent`
// user setup (Dockerfile.runtime-base Layer 5).

/**
 * PATH used by the bootstrap install / setup snippets and the agent-facing
 * dry-run tool. Order matters: `/data/mise/shims` comes first so a `dotnet`
 * installed via mise wins over any stray system-package version.
 */
export const BOOTSTRAP_DEFAULT_PATH =
  '/data/mise/shims:/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin'

/**
 * HOME for bootstrap snippet exec. `process.env.HOME` is preferred when set
 * (tests/dev override) — fall back to the runtime image's agent home.
 */
export const BOOTSTRAP_DEFAULT_HOME = '/home/agent'

/**
 * Build the env block we hand to `IExecutor.run({ env })` for any snippet
 * derived from a runtime spec. Centralized so the install / setup / dry-run
 * code paths can never drift apart.
 *
 * Caller may layer extra keys (e.g. `GIT_SSH_COMMAND` from `CloningRepoStage`)
 * on top by spreading.
 */
export function bootstrapEnv(): Record<string, string> {
  return {
    PATH: BOOTSTRAP_DEFAULT_PATH,
    HOME: process.env['HOME'] ?? BOOTSTRAP_DEFAULT_HOME,
  }
}

# @glenn/daemon

## What it is

A Node 20+ ESM TypeScript process that runs once per runtime Machine. It is the
only thing on the box that talks to both the platform (SignalR back to the main
.NET API) and the model (**Cursor SDK** via `@cursor/sdk`, lazy-imported per turn). Everything user-visible —
turn lifecycle, heartbeat, disk pressure, quiet mode, custom tools, graceful
drain — sits behind this binary. The daemon owns no project code, no database,
no HTTP API of its own; it is a translator with a lifecycle.

Architecture and ops: `.claude/skills/runtime-environment/SKILL.md`. This README
is for contributors adding daemon code.

## Module map

| Module | File | Responsibility |
|---|---|---|
| Version constant | `src/version.ts` | esbuild-injected `DAEMON_VERSION` string. |
| Config | `src/config/DaemonConfig.ts` | Reads env, validates, holds the (rotatable, redacted) runtime token. |
| SignalR client | `src/signalr/SignalRClient.ts` | Typed wrapper over `@microsoft/signalr`; reconnect policy + token-rotation factory. |
| SignalR retry | `src/signalr/retryPolicy.ts` | `IndefiniteReconnectPolicy` — `[0, 2s, 5s, 10s, 30s]` then 30s forever. |
| SignalR types | `src/signalr/types.ts` | TS mirrors of the .NET hub DTOs (camelCase per the JSON serializer setup). |
| Heartbeat | `src/heartbeat/HeartbeatModule.ts` | Periodic `Heartbeat(payload)` send, gather callback, immediate first tick. |
| Disk monitor | `src/disk/DiskMonitor.ts` | Polls `statfs`, emits `pressure` only on threshold transitions. |
| Bootstrap | `src/bootstrap/BootstrapOrchestrator.ts` | Sequential stage runner with `[1s, 2s, 4s, 8s, 30s]` retry; AbortSignal-aware. |
| Bootstrap stages | `src/bootstrap/stages/*.ts` | `ConnectingStage` → `VerifyEnvStage` → `FetchingStage` → `WritingConfigStage` → `InstallStage` → `CloningRepoStage` → `RunningSetupStage` → `StartingServicesStage` → `ReportReadyStage`. Install/Setup/StartingServices are **non-critical** (spec can degrade Online); see `self-healing-runtime` skill. |
| Turn runner | `src/turn/TurnRunner.ts` | Owns the active Cursor agent turn; idle/running/canceling state; emits `idle`/`activity`. |
| Cursor SDK adapter | `src/turn/CursorFactory.ts` | Builds per-turn `Agent.create` / `Agent.resume` via `@cursor/sdk`; maps SDK messages to wire events. |
| Event mapper | `src/turn/CursorEventMapper.ts` | Translates Cursor `SDKMessage` frames into daemon `TurnEvent` shapes. |
| Turn types | `src/turn/types.ts` | `CustomTool`, `ToolContext`, `AfterPromptHook`. |
| MCP registry | `src/mcp/McpRegistry.ts` | Project-scoped HTTP MCP servers wired into Cursor SDK `mcpServers` each turn. |
| Daemon tools MCP | `src/mcp/DaemonToolsMcpServer.ts` | In-process MCP server for platform custom tools (`propose_runtime_spec`, etc.). |
| Quiet mode | `src/turn/QuietModeManager.ts` | Observes idle/activity; sleeps daemon background work after quiet timeout; wakes synchronously on activity. |
| Custom tools | `src/tools/CustomTools.ts` | `propose_runtime_spec` (Ajv-validated) and `restart_service` (regex-guarded `execFile`). |
| Shutdown | `src/lifecycle/ShutdownCoordinator.ts` | SIGTERM/SIGINT → drain turn → stop modules → emit `daemon_shutting_down` → `signalr.stop` → exit. |
| Protocol gate | `src/protocol/versionRequirements.ts` | `requireProtocol(method, daemonVersion)` against `MIN_PROTOCOL_VERSIONS` table. |
| Composition root | `src/main.ts` | Scene 1 ordering, factory-overridable `runMain(deps)` for tests, CLI entry. |

## Startup sequence

Verbatim Scene 1, with the file each step lives in. `main.ts` is the only place
this order changes.

1. **Config** — `DaemonConfig.fromEnv()` (`config/DaemonConfig.ts`). Aggregates
   every problem before throwing `DaemonConfigError`. On failure: log the
   aggregate, `exit(1)`.
2. **Logger** — pino, pretty in dev, JSON in prod, level from config.
3. **SignalR up** — `SignalRClient` (`signalr/SignalRClient.ts`); `await signalr.start()`.
   On failure: log, `exit(1)` so supervisord respawns.
4. **Bootstrap built** — `BootstrapOrchestrator` (`bootstrap/BootstrapOrchestrator.ts`)
   with the full stage pipeline (see module map above).
5. **Modules built** — `DiskMonitor`, `TurnRunner`, `HeartbeatModule`,
   `QuietModeManager`, `ShutdownCoordinator`. Deps wired by the factories in
   `main.ts`.
6. **`shutdownCoordinator.install({ cancelInFlightBootstrap })`** — *before*
   any I/O past this point. A SIGTERM during bootstrap aborts the
   bootstrap signal cleanly instead of leaking into stage retry sleeps.
7. **TurnRunner wired early** — `StartTurn` / `CancelTurn` handlers registered
   *before* bootstrap so a synchronous `RuntimeReady` fan-out cannot drop
   queued sessions.
8. **Bootstrap run** — `bootstrap.start(abortSignal)`. Reject → emit
   `bootstrap_failed` (best-effort), `exit(1)`.
9. **Remaining inbound handlers wired** —
   * `UpdateConfig` → `config.rotateToken(p.runtimeToken)` (token rotation v1)
   * `RestartService` → `restart_service` custom tool, server-initiated path.
10. **Background loops start** — `heartbeat.start()`, `disk.start()`,
    `quietMode.start()`.
11. **`logger.info('daemon ready')`** — the only "we're up" log line.

The authoritative startup order (including MCP registry wiring, git/hook
subsystems, and Cursor factory setup) lives in the step comments at the top of
`src/main.ts`.

The composition-root test (`src/main.test.ts`) records each step into a shared
order array and asserts the sequence — particularly that SIGTERM is bound
before heartbeat starts.

## Adding a custom tool

1. **Schema.** Add a JSON Schema entry to `CUSTOM_TOOL_JSON_SCHEMAS` in
   `src/tools/CustomTools.ts` and a parallel Zod raw shape to
   `CUSTOM_TOOL_ZOD_SHAPES`. Two sources of truth, by design: Ajv is the
   contract validator (server-initiated calls), Zod is what the SDK's `tool()`
   helper insists on. Keep the two in lockstep.

2. **Register.** Add a `{ name, description, run }` entry to the array returned
   by `buildCustomTools(deps)` in the same file. `run(args, ctx)` receives the
   already-validated args and a `ToolContext` (`{ signalr, config, sessionId,
   turnId }`). Throw on unrecoverable; return a structured result on
   recoverable. Side effects belong inside `run`.

3. **Document.** Add a row to the *Module map* if the tool grows beyond a
   handful of lines, otherwise leave it inline. Update the spec backlog if the
   tool implies new wire-format fields.

`DaemonToolsMcpServer` exposes custom tools to the Cursor SDK on each turn — no
further wiring needed for the in-process MCP path. For the server-initiated path
(e.g. `RestartService`), `main.ts` looks the tool up by name; it finds the new
one too if you keep the name string consistent.

## Quiet mode

The daemon is process-resident on a Machine that may sit idle for hours between
turns. `QuietModeManager` (`src/turn/QuietModeManager.ts`) observes the
TurnRunner's `idle`/`activity` events and, after `quietTimeoutMs` of continuous
idle, asks the heartbeat and disk modules to sleep (longer interval, no work
between ticks). Any `activity` event wakes everything synchronously — no
debounce, no race window: by the time `TurnRunner.start` returns, the
heartbeat is back to fast cadence.

Observability: the manager logs `quiet:enter` and `quiet:exit` at info level.
Heartbeat payload carries enough state for the platform side to confirm; if
not, ship-and-fix later — the spec deliberately keeps the wire format minimal
(`emittedAt, daemonVersion, cpuPercent, memoryUsedMb`) until something forces a
field.

## Build + test

```bash
# from packages/daemon
npm install

npm run typecheck     # tsc --noEmit, strict + noUncheckedIndexedAccess + exactOptionalPropertyTypes
npm test              # vitest run --passWithNoTests
npm run build         # esbuild → dist/main.js (single bundled ESM file)
npm run dev           # esbuild watch
```

The build injects `__VERSION__` from `package.json` via esbuild `define`; the
runtime constant lives in `src/version.ts`. The dev script bundles to `dist/`
on every save — there is no `ts-node` or loader hack on the production path,
ever.

Vitest runs in fake-timers mode for anything timer-driven; we use
`vi.advanceTimersByTimeAsync(ms)` to flush microtasks. We do **not** mock
`@cursor/sdk` or `@microsoft/signalr` — instead, every module that touches them
takes a factory dep that tests stub with a hand-rolled fake (often `extends
EventEmitter`). The pattern is consistent across the codebase; copy from a
sibling test file when adding new ones.

## Resource budget

These are budgets, not benchmarks — exceed them and we owe an investigation,
not a new rule.

* **During a turn:** under 250 MB RSS, under 1.5 vCPU sustained.
* **Quiet (no turn for >60s):** under 80 MB RSS, under 0.05 vCPU steady.
* **Heartbeat send overhead:** under 5 ms per tick at p99.
* **Cold start to `daemon ready`:** under 5 s on the standard Fly machine size,
  excluding bootstrap stages (which the runtime-bootstrap spec governs).

If a feature can't fit the quiet budget, it goes behind quiet-mode gating —
not into the steady-state path.

## Versioning

The daemon ships a single string, `DAEMON_VERSION`, baked at build time from
`package.json`. The platform reads it off the heartbeat payload; a daemon never
needs to ask "what version am I".

Forward compatibility is handled in one place: `src/protocol/versionRequirements.ts`.
`MIN_PROTOCOL_VERSIONS` lists hub methods that require a minimum daemon
version. `requireProtocol(method, DAEMON_VERSION)` returns `{ ok: true }` if
the method is unlisted (most are) or if the current version satisfies the
floor. Otherwise: `{ ok: false, required, current }`. The composition root
emits `protocol_version_mismatch` (best-effort) and refuses the operation.

The table starts with one demonstration entry. Every time we ship a hub method
that depends on a daemon-side feature added in the same release, add a row.
Removing a row is allowed only when the platform stops calling the method
entirely.

## What this daemon is **not**

* **Not an HTTP server.** No express, no fastify, no inbound port. SignalR is
  the only wire. If you need an inbound endpoint, build it on the .NET side
  and have the daemon call out.
* **Not a database client.** No `pg`, no Prisma, no migration runner. Project
  state lives in the .NET API; runtime state lives in the platform DB.
* **Not project code.** The daemon does not run the user's app, build their
  project, or reason about their language. The Cursor SDK + custom tools do that —
  the daemon hosts them.
* **Not autonomous.** State-changing work is initiated by the platform (SignalR)
  or by the model via custom tools. The daemon does not run unsupervised
  background reconciliation — but it **does** participate in the platform's
  self-healing spec repair loop when the operator triggers repair.
* **Not multi-tenant.** One daemon, one Machine, one runtime token. Token
  rotation is in-place; tenant separation is the platform's job.

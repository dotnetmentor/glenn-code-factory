---
name: runtime-environment
description: Architecture and operating manual for project runtimes — Fly machines, daemon bootstrap, RuntimeSpec, SignalR hub contract, supervisord layout, and persistence rules. Use when (1) anything touches packages/daemon or RuntimeLifecycle/FlyManagement/RuntimeBootstrap/RuntimeTokens/DaemonVersions, (2) adding or debugging a runtime service in the spec, (3) a runtime is stuck and you need the system map, (4) adding a SignalR hub method or runtime event, (5) modifying the runtime base image or daemon bundle pipeline, (6) tempted to apt-install inside a runtime without understanding persist_rootfs.
---

# Runtime Environment — Architecture & Operating Manual

Every project gets a **Runtime** — a Fly.io machine with a persistent `/data` volume, running a Node **daemon** that brings up services from a JSON spec. The daemon talks home to the main .NET API via SignalR.

> **Paths:** On a managed platform the repo may live at `/data/project/repo`. Locally, substitute your clone root.

## TL;DR — mental model

```
Main API (.NET)
  RuntimeProvisioner (Hangfire) → Fly Machines API
  SignalR /hubs/runtime ◄──────── daemon (JWT rt_runtime claim)

Fly Machine
  Image: runtime-base (read-only)
  Volume: /data (persistent)
  Env: GLENN_RUNTIME_TOKEN, MAIN_API_URL, DAEMON_BUNDLE_*
  supervisord → bootstrap-daemon.sh → node daemon.js
              → user services (postgres, redis, …)
```

**Three persistence layers** (most important concept):

| Layer | What | Survives |
|-------|------|----------|
| OCI image | Node, supervisord, postgres, mise, Playwright | Read-only at runtime; rebuilt rarely |
| `/data` volume | repo, env files, install hashes, mise toolchains, service data | Forever (Fly volume) |
| Rootfs overlay | apt-installed packages from spec install scripts | **Only if** `persist_rootfs = "always"` on the machine |

Without `persist_rootfs="always"`, install-hash on `/data` says "installed" but binaries on ephemeral rootfs are gone → `ENOENT` → FATAL loop.

## Lifecycle state machine

**Files:** `RuntimeStateMachine.cs`, `RuntimeState.cs` (12 states).

```
Pending → Booting → Bootstrapping → Online → Suspending → Suspended
                         ↑              │                      ↓ Waking → Online
                         └── race-closer (Booting→Online)     │
Crashed ← any non-terminal              Failed ← admin reset  │
Deleting → Deleted (terminal)
```

**Rules:**
- All transitions via `ProjectRuntime.TransitionTo()` — never assign `runtime.State` directly.
- `Booting → Online` is **deliberate** (daemon `RuntimeReady` before FSM sees Bootstrapping). Don't remove.
- `Suspended` has **no direct edge to Crashed** — wake first, then force-respawn from Online.

## Cold-start provisioning

**File:** `RuntimeProvisionerJob.cs` (Hangfire, batch 10, 60s lock).

1. Resolve daemon bundle (`ResolveDaemonVersionQuery("stable")`)
2. Create Fly volume (skipped on copy-branch fork). Name: `"vol_" + Guid[..30]`
3. Mint runtime JWT (`IRuntimeTokenService`) — audit row written **before** token returned
4. `CreateMachineRequest` with env (§ below), `PersistRootfs = "always"`, volume at `/data`
5. POST Fly Machines API → transition `Pending → Booting`

**Daemon is NOT in the image.** `bootstrap-daemon.sh` downloads tarball from `DAEMON_BUNDLE_URL`, verifies SHA256, caches at `/opt/agent/.bundle.sha256`, extracts, `exec node daemon.js`.

## Env vars stamped on every machine

Set by `RuntimeProvisionerJob` and `RespawnRuntimeJob`:

| Key | Purpose |
|-----|---------|
| `RUNTIME_ID` | `ProjectRuntime.Id` |
| `GLENN_RUNTIME_TOKEN` | JWT for SignalR/HTTP (`rt_runtime`, `rt_project`, `rt_branch`, `rt_tenant`, `rt_scope`). 7-day default. Daemon does **not** self-refresh. |
| `MAIN_API_URL` | SignalR + HTTP home URL |
| `DAEMON_VERSION` | Informational version string |
| `DAEMON_BUNDLE_URL` | Download URL for bootstrap script |
| `DAEMON_BUNDLE_SHA256` | Expected hash — mismatch aborts bootstrap |

Fly app name and registry image path come from `SystemSettings` (`Fly:AppName`, active `RuntimeImages.Registry`) — configure per deployment, not hardcoded in repo.

## Bootstrap stages

**File:** `BootstrapOrchestrator.ts` — `MAX_ATTEMPTS = 5`, backoff `[1s, 2s, 4s, 8s, 30s]`.

| Stage | File | Critical? |
|-------|------|-----------|
| Connecting | `ConnectingStage.ts` | Yes — wait for SignalR |
| VerifyEnv | `VerifyEnvStage.ts` | Yes |
| Fetching | `FetchingStage.ts` | Yes — `GetBootstrap` → payload (wire version `v2`) |
| WritingConfig | `WritingConfigStage.ts` | Yes — writes `/data/.glenn/{env,hooks,mcp}.json` |
| Install | `InstallStage.ts` | **No** — spec install; failure → BootIssue, continues |
| CloningRepo | `CloningRepoStage.ts` | Yes — GitHub Basic auth (not Bearer) |
| RunningSetup | `RunningSetupStage.ts` | **No** — spec setup script |
| StartingServices | `StartingServicesStage.ts` | **No** — supervisord + healthchecks |
| ReportReady | `ReportReadyStage.ts` | Yes — `runtimeReady()` → Online |

Non-critical stages (`critical: false`): deterministic failure records a `BootIssue` and **continues** so runtime reaches Online with `SpecHealth = Degraded`. Transient failures still retry. See `.claude/skills/self-healing-runtime/SKILL.md`.

## Runtime spec — two shapes

| Context | Shape | File |
|---------|-------|------|
| Bootstrap wire payload | `BootstrapPayloadV2` — daemon accepts `version: 'v2'` only (`FetchingStage.ts`) | Server expands V3 → V2 for daemon |
| Proposals / templates / UI | `RuntimeSpecV3` — `{ version: 3, services: [{ kind, name, values }], install?, setup? }` | `RuntimePresets/Contracts/RuntimeSpecV3.cs` |

**V2 service fields** (what the daemon executes after server expansion):

```csharp
ServiceSpec(string Name, string Command, string? User, bool? Autorestart,
            Dictionary<string,string>? Env, HealthcheckSpec? Healthcheck,
            string? Install, string? InstallVerify)
```

Install snippets must be idempotent: `command -v` guards, `/data/<svc>/.initialized` sentinels, final `chown` to runtime user.

## Proposal / approve / apply

```
Daemon/agent → POST /api/runtimes/{id}/proposals (RuntimeSpecV3, runtime JWT)
User → POST /api/projects/{projectId}/proposals/{id}/approve (user JWT)
→ SpecDelta.Compute → SignalR push → RuntimeSpecApplier (daemon)
→ RuntimeSpecDeltaApplied ack
```

**Applier order** (`RuntimeSpecApplier.ts`): top-level install → per-service install + add/restart → remove services (stop, remove, unlink conf, purge hash) → setup re-run → ack.

**Bootstrap reconcile:** `StartingServicesStage` calls `supervisord.reconcileServices()` to drop orphan conf files on cold boot.

## persist_rootfs + installVerify

**Default:** `MachineGuest.PersistRootfs = "always"` in `CreateMachineRequest.cs`. Set at machine **creation** — cannot retrofit; respawn to pick up.

**installVerify:** Optional bash predicate per install scope. On hash-skip path, non-zero exit forces full re-install. Handles the ~1% case where host migration wipes rootfs but `/data` install-hash survives.

## SignalR wire contract (JSON.stringify trap)

`RuntimeEventPayloadDto.Payload` is a **string**, not `JsonElement`. Daemon must `JSON.stringify(envelope.payload)` in `RuntimeEventEmitter.ts#sendNow` before `invoke`. Sending a JS object → `InvalidDataException: Error binding arguments`.

`ReportSpecHealth` uses raw `invoke('ReportSpecHealth', JSON.stringify(report))` in `SignalRClient.ts` — no TypedSignalR regen required for that method.

## Supervisord conf-dir (common gotcha)

Base image includes `/data/.glenn/supervisor.d/*.conf` but `SupervisordController` defaults to `/etc/supervisor/conf.d`.

**Both** construction sites in `main.ts` must override:

```ts
confDir: '/data/.glenn/supervisor.d'
```

## Hub methods (daemon → server)

`RuntimeHub.cs` — all resolve `RuntimeId` from signed `rt_runtime` claim (daemon cannot impersonate another runtime):

| Method | Purpose |
|--------|---------|
| `Heartbeat` | Liveness |
| `GetBootstrap` | Bootstrap payload |
| `GetSecrets` / `GetRepoAccessToken` | Secrets + git auth |
| `RuntimeReady` | Flip to Online |
| `ReportSpecHealth` | SpecHealth after boot |
| `RecordRuntimeEvent` | Structured events (§ stringify) |
| `RuntimeSpecDeltaApplied` | Post-apply ack |

## JWT / tokens

**Service:** `RuntimeTokenService.cs` — issuer `glenn-main-api`, audience `glenn-runtime`, 7-day default, audit-before-issuance, `kid` stripped on validation for key rotation.

## Reconciler

`RuntimeReconcilerJob` — every 1 min. Drift fixer only: Fly machine gone → `Crashed` if legal. Skips `Pending` and `Deleted`.

## Image & daemon publish

| Artifact | Build | Publish |
|----------|-------|---------|
| Runtime base image | `Dockerfile.runtime-base` | `publish-runtime-image.sh` (local docker) or `publish-runtime-image-remote.sh` (flyctl remote) |
| Daemon bundle | `packages/daemon` esbuild | `publish-daemon.sh` → storage + `POST /api/daemon-versions` |

Publish scripts generate a **temporary** `.fly.runtime-base.toml` at repo root during remote builds — there is no committed fly.toml needed. Configure `APP`, `REGISTRY`, `IMAGE_NAME` via env vars on the publish scripts.

`GET /api/daemon-versions/resolve?channel=stable` is `[AllowAnonymous]` (daemon has no token at cold boot).

## Known gotchas (bug graveyard)

| Gotcha | Fix |
|--------|-----|
| JSON.stringify on event payload | Pre-serialize in daemon |
| persist_rootfs missing | Respawn machine with default |
| Supervisord conf-dir mismatch | Override both `main.ts` sites |
| GitHub Basic not Bearer | `CloningRepoStage` |
| 2 GiB minimum RAM | `@cursor/sdk` + sqlite3 binding; 256 MB OOMs silently |
| Volume name length | `"vol_" + guid[..30]` |
| Bare sha256 in RuntimeImages | Store full `registry/.../image@sha256:...` ref |
| Host-only registry string | Must include image name: `registry.fly.io/my-runtime-base` |

## Admin endpoints

`api/admin/runtimes` — SuperAdmin only: list, detail, reset, force-suspend, force-delete, force-respawn, force-rebootstrap.

## Related skills

| Skill | When |
|-------|------|
| `daemon-deploy` | SignalR contract / daemon code changed |
| `runtime-deployment` | Ship base image, provision, smoke test |
| `runtime-debug` | SSH, logs, hot-swap bundle |
| `self-healing-runtime` | Degraded Online, repair loop, SpecHealth |

## Quick reference paths

| Thing | Path |
|-------|------|
| State machine | `Features/RuntimeLifecycle/RuntimeStateMachine.cs` |
| Provisioner | `Features/RuntimeLifecycle/Jobs/RuntimeProvisionerJob.cs` |
| RuntimeSpecV3 | `Features/RuntimePresets/Contracts/RuntimeSpecV3.cs` |
| Spec applier | `packages/daemon/src/runtime/RuntimeSpecApplier.ts` |
| Bootstrap orchestrator | `packages/daemon/src/bootstrap/BootstrapOrchestrator.ts` |
| Event emitter | `packages/daemon/src/events/RuntimeEventEmitter.ts` |
| Runtime hub | `Features/SignalR/Hubs/RuntimeHub.cs` |
| Bootstrap script | `docker/bootstrap-daemon.sh` |

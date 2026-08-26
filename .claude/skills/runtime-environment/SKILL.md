---
name: runtime-environment
description: Architecture and operating manual for project runtimes — Box VMs (box.ascii.dev), daemon bootstrap, RuntimeSpec, SignalR hub contract, supervisord layout, TTL guardrail, and persistence rules. Use when (1) anything touches packages/daemon or RuntimeLifecycle/BoxManagement/RuntimeBootstrap/RuntimeTokens/RuntimeTemplates, (2) adding or debugging a runtime service in the spec, (3) a runtime is stuck and you need the system map, (4) adding a SignalR hub method or runtime event, (5) modifying the golden template or daemon bundle pipeline, (6) reasoning about box fork/stop/resume, TTL, or the machine-start budget.
---

# Runtime Environment — Architecture & Operating Manual

Every project branch gets a **Runtime** — a Box VM (box.ascii.dev) forked from a
**golden template box**, running a Node **daemon** that brings up services from a
JSON spec. The daemon talks home to the main .NET API via SignalR. The box's disk
IS the persistence: it survives stop/resume/fork, so there is no separate volume.

> **Paths:** On a managed platform the repo may live at `/data/project/repo`. Locally, substitute your clone root.

## TL;DR — mental model

```
Main API (.NET)
  RuntimeProvisionerJob (Hangfire) → Box API (fork template → runtime box)
  SignalR /hubs/runtime ◄──────────── daemon (JWT rt_runtime claim)

Box VM (full Ubuntu, systemd)
  Forked from: golden template box (RuntimeTemplates, newest Active)
  Identity: per-fork env + /etc/glenn/runtime.env (refresh channel)
  systemd: glenn-daemon.service → entrypoint.sh → supervisord
             supervisord → bootstrap-daemon.sh → node daemon.js
                         → user services (postgres, redis, …)
  TTL: finite, re-armed every 30 min by BoxTtlExtenderJob (orphan guardrail)
```

**Persistence (one layer, unlike the Fly era's three):** the box disk holds
everything — apt installs, `/data` (repo, service data, mise, install hashes),
`/opt/agent`. Stop archives the box with a snapshot (billing pauses); resume and
fork restore/inherit the whole disk. The old `persist_rootfs` / `installVerify`
split-brain class of bug cannot exist here.

## The TTL guardrail (why runtimes can never bill unattended)

Every box is created with a finite `ttlSeconds` (`Box:DefaultTtlSeconds`, default
6 h). If the control plane loses track of a box — crash, deleted row, platform
outage — the box **archives itself** when the TTL lapses and billing stops.
`BoxTtlExtenderJob` (every 30 min) re-arms the TTL for every runtime in a live
state, so healthy boxes never hit the deadline. Wake paths re-arm explicitly.
Never "fix" a TTL by setting it to null — that reopens the orphan-cost hole this
exists to close.

## The machine-start budget

Box caps account-wide machine starts (~600/hr, ~1,500/day); **create, fork, and
resume each count as one**. Design consequences already encoded:
- wake happens per session (wake-on-connect), never per message;
- the provisioner batch is 10/min; transient budget errors leave rows Pending
  (`BoxRuntimeProvisioning.IsTransient`) instead of Failed;
- CopyBranch costs 2 starts (fork + source resume).

## Lifecycle state machine

**Files:** `RuntimeStateMachine.cs`, `RuntimeState.cs` (12 states).

```
Pending → Booting → Bootstrapping → Online → Suspending → Suspended
                         ↑              │                      ↓ Waking → Online
                         └── race-closer (Booting→Online)      │
Crashed ← any non-terminal              Failed ← admin reset   │
Deleting → Deleted (terminal)
```

**Rules:**
- All transitions via `ProjectRuntime.TransitionTo()` — never assign `runtime.State` directly.
- Box has **no webhooks** — `RuntimeReconcilerJob` (1 min) is the only driver of
  box-observation edges (Booting→Bootstrapping when the VM is up,
  Suspending→Suspended when the stop lands). Box statuses: `provisioning`,
  `ready`/`idle`/`running` (up), `archived` (stopped+snapshot), `error`.
- Only the daemon's `RuntimeReady` hub call flips Bootstrapping → Online — a VM
  being up says nothing about the daemon having bootstrapped.
- `Suspended` has **no direct edge to Crashed** — wake first, then force-respawn from Online.

## Cold-start provisioning

**File:** `RuntimeProvisionerJob.cs` (Hangfire, batch 10, 60s lock). Three shapes:

1. **Fresh fork** (no `BoxId`): resolve newest Active `RuntimeTemplate`, mint
   runtime JWT (audit-before-issuance), build env
   (`BoxRuntimeProvisioning.BuildRuntimeEnvAsync` — shared with respawn), then
   `POST /boxes/{template}/fork` with `name: rt-{runtimeId:N}`, size from
   `BoxSizeMapper.FromSpec(cpus, memoryMb)` (small 2/4 · default 4/8 · large
   8/16, rounds UP), the env dict, `noEnv: true` (fork sees none of the platform
   account's secrets), and the TTL. Stamp `BoxId` + `TemplateBoxId` → Booting.
2. **Reboot** (`BoxId` set, size unchanged — restart / CopyBranch handoff):
   resume if archived → Booting → re-arm TTL → wait-up → refresh
   `/etc/glenn/runtime.env` + `systemctl restart glenn-daemon` via the commands
   API (fresh JWT). Env refresh is best-effort; a stale JWT surfaces as a failed
   SignalR connect and the watcher schedules a respawn.
3. **Disk-preserving resize** (`BoxId` set, size tier changed): stop (snapshot)
   → fork the snapshot at the new size with fresh env → delete the old box.

**Daemon is NOT in the template's snapshot as a pinned version.**
`bootstrap-daemon.sh` (installed by the template) resolves + downloads the bundle
from the main API at every daemon start, so a new publish auto-rolls-out on the
next daemon restart.

## Env contract (stamped per fork, refreshed via env file)

| Key | Purpose |
|-----|---------|
| `RUNTIME_ID` | `ProjectRuntime.Id` |
| `GLENN_RUNTIME_TOKEN` | JWT for SignalR/HTTP (`rt_runtime`, `rt_project`, `rt_tenant`, `rt_scope`). 7-day default; **refreshed via `/etc/glenn/runtime.env` on every reboot/respawn**. |
| `MAIN_API_URL` | SignalR + HTTP home URL AND the bundle-resolve endpoint |
| `DAEMON_VERSION` / `DAEMON_BUNDLE_URL` / `DAEMON_BUNDLE_SHA256` | Informational stamps — bootstrap re-resolves at boot |
| `TUNNEL_TOKEN` / `PREVIEW_PORT` / `PREVIEW_HOSTNAME` | Cloudflare preview tunnel trio (when the branch has an assigned subdomain) |

Delivery: per-fork env at fork time (primary) + `/etc/glenn/runtime.env`
(refresh channel, written via `POST /boxes/{id}/commands`). The
`glenn-daemon.service` unit loads both (`EnvironmentFile=-/etc/environment` then
`-/etc/glenn/runtime.env`). `scripts/box-smoke-test.sh` item 10 verifies where
Box actually lands per-fork env — check it after any Box platform change.

## Respawn (crash recovery)

**File:** `RespawnRuntimeJob.cs`. Box-native respawn is a clean VM reboot of the
SAME box: stop if up (wedged VM → fresh snapshot) → resume → TTL re-arm → env
refresh with fresh JWT → Crashed → Booting. The disk (repo, DB data) survives by
construction. Only when the box has vanished (404) does it fork fresh from the
template — accepting the previous disk state is gone.

## Branch fork (CopyBranch)

`CopyBranchHandler`: stop the source box if up (forks take the LATEST SNAPSHOT,
and snapshots happen on stop — this pins current disk state, costing the source
a few seconds of pause) → `POST /boxes/{sourceBox}/fork` with identity-only env
+ `noEnv` → resume the source → new `ProjectRuntime` row with `BoxId` already
set → provisioner's reboot path delivers the real env + JWT on first tick.
Compensation stack deletes the forked box + GitHub ref on downstream failure.

## Runtime spec — two shapes (unchanged from the Fly era)

| Context | Shape | File |
|---------|-------|------|
| Bootstrap wire payload | `BootstrapPayloadV2` — daemon accepts `version: 'v2'` only (`FetchingStage.ts`) | Server expands V3 → V2 for daemon |
| Proposals / templates / UI | `RuntimeSpecV3` — `{ version: 3, services: [{ kind, name, values }], install?, setup? }` | `RuntimePresets/Contracts/RuntimeSpecV3.cs` |

Install snippets must stay idempotent (`command -v` guards, sentinels) — a
respawned box re-runs bootstrap against a disk where installs already happened.

## Bootstrap stages (daemon-side, unchanged)

**File:** `BootstrapOrchestrator.ts` — Connecting → VerifyEnv → Fetching →
WritingConfig → Install (non-critical) → CloningRepo → RunningSetup
(non-critical) → StartingServices (non-critical) → ReportReady. Non-critical
failures record a `BootIssue` and continue → Online with `SpecHealth=Degraded`
(see `self-healing-runtime` skill).

## Hub methods (daemon → server) — unchanged

`RuntimeHub.cs`: `Heartbeat`, `GetBootstrap`, `GetSecrets`/`GetRepoAccessToken`,
`RuntimeReady`, `ReportSpecHealth`, `RecordRuntimeEvent` (payload must be
pre-`JSON.stringify`d — see wire-contract trap below), `RuntimeSpecDeltaApplied`.

### SignalR wire contract (JSON.stringify trap)

`RuntimeEventPayloadDto.Payload` is a **string**, not `JsonElement`. Daemon must
`JSON.stringify(envelope.payload)` in `RuntimeEventEmitter.ts#sendNow` before
`invoke`. Sending a JS object → `InvalidDataException: Error binding arguments`.

### Supervisord conf-dir (still a gotcha)

Template installs `/data/.glenn/supervisor.d` as the drop-in dir but
`SupervisordController` defaults to `/etc/supervisor/conf.d`. **Both**
construction sites in `main.ts` must override `confDir: '/data/.glenn/supervisor.d'`.

## Golden template & daemon publish

| Artifact | Build | Publish |
|----------|-------|---------|
| Golden template box | `scripts/build-box-template.sh` (provisions a live box via the Box API, stops it to snapshot) | auto-registers via `POST /api/admin/runtime-templates` (CI publish key) or manually in Super Admin → Runtime Templates |
| Daemon bundle | `packages/daemon` esbuild | `publish-daemon.sh` → storage + `POST /api/daemon-versions` |

Template boxes must STAY STOPPED (forks take the latest snapshot; a running
template bills and drifts). `BoxAdminController` refuses to delete a registered
template; yank it first.

### Agent visual self-validation (`snap-preview`)

The template bakes Playwright + Chromium (`PLAYWRIGHT_BROWSERS_PATH=/opt/playwright-browsers`)
and `/usr/local/bin/snap-preview` (`docker/snap-preview{,.cjs}`): headless-Chromium
screenshot + console/page-error JSON + WebGL probe for the in-box agent to check its
own frontend work (taught in `packages/daemon/src/harness/harness.md`). Boxes have
**no GPU** — WebGL/Three.js renders via SwiftShader (flags `--use-angle=swiftshader
--enable-unsafe-swiftshader`): pixel-correct, slow, never a perf signal. End users
render client-side through the tunnel, so they're unaffected. The template build and
`box-smoke-test.sh` §12b both hard-verify the SwiftShader path draws real pixels. `GET /api/daemon-versions/resolve?channel=stable` is
`[AllowAnonymous]` (bootstrap has no token pre-connect... it does have the JWT,
but resolution happens before auth is exercised).

## Reconciler & observability

- `RuntimeReconcilerJob` (1 min) — state driver + drift fixer (see above).
- `BoxTtlExtenderJob` (30 min) — TTL guardrail re-arm.
- `BoxDriftPollerJob` (1 min) — emits `RuntimeBoxDriftDetected` events (no state mutation).
- `RuntimeDriftQueryService` / `DriftEvaluator` — Super Admin → Runtime Monitor
  rows (rules: `BoxVanished`, `OrphanBox`, `StateMismatch_OnlineButArchived`,
  `StateMismatch_SuspendedButRunning`, `StateMismatch_OnlineButNotUp`,
  `StuckInTransition`, `StaleHeartbeat`).
- `RuntimeBoxSnapshotService` — the runtime drawer's "our view vs Box" tab.
- Every Box API call lands one `BoxOperation` audit row (idempotency-keyed
  replay window 60 s) — `api/admin/box/operations` pages through them.

## Known gotchas (bug graveyard — Box era)

| Gotcha | Fix |
|--------|-----|
| JSON.stringify on event payload | Pre-serialize in daemon |
| Supervisord conf-dir mismatch | Override both `main.ts` sites |
| GitHub Basic not Bearer | `CloningRepoStage` |
| `@cursor/sdk` + sqlite3 needs ≥2 GiB | Even the `small` tier (4 GB) clears it — `BoxSizeMapper` rounds up |
| Fork of a running box takes the LAST snapshot, not live disk | Stop → fork → resume (CopyBranch does this) |
| Per-fork env is immutable after creation | Fresh JWTs travel via `/etc/glenn/runtime.env` + `systemctl restart glenn-daemon` |
| Start budget exhaustion (429 / daily_limit) | Transient — rows stay Pending; never mark Failed |
| Only systemd services survive stop/resume | Everything must hang off `glenn-daemon.service`; hand-run processes die |
| Template box left running | Bills + snapshot drift; keep archived |

## Admin endpoints

- `api/admin/runtimes` — SuperAdmin: list, detail, box-snapshot, drift, reset,
  force-suspend, force-delete, force-respawn, force-rebootstrap.
- `api/admin/box` — SuperAdmin: boxes/snapshots cleanup, test-connection, operations log.
- `api/admin/runtime-templates` — template catalog + live candidate-box discovery.

## Quick reference paths

| Thing | Path |
|-------|------|
| Box client | `Source/Features/BoxManagement/BoxClient.cs` |
| Provisioning helpers | `Features/RuntimeLifecycle/Provisioning/BoxRuntimeProvisioning.cs` |
| Provisioner | `Features/RuntimeLifecycle/Jobs/RuntimeProvisionerJob.cs` |
| Respawn | `Features/RuntimeLifecycle/Jobs/RespawnRuntimeJob.cs` |
| Reconciler | `Features/RuntimeLifecycle/Jobs/RuntimeReconcilerJob.cs` |
| TTL guardrail | `Features/RuntimeLifecycle/Jobs/BoxTtlExtenderJob.cs` |
| State machine | `Features/RuntimeLifecycle/RuntimeStateMachine.cs` |
| Templates | `Features/RuntimeTemplates/` |
| RuntimeSpecV3 | `Features/RuntimePresets/Contracts/RuntimeSpecV3.cs` |
| Spec applier | `packages/daemon/src/runtime/RuntimeSpecApplier.ts` |
| Bootstrap orchestrator | `packages/daemon/src/bootstrap/BootstrapOrchestrator.ts` |
| Runtime hub | `Features/SignalR/Hubs/RuntimeHub.cs` |
| Bootstrap script | `docker/bootstrap-daemon.sh` |
| Template builder | `scripts/build-box-template.sh` |
| Wire-assumption pinning | `scripts/box-smoke-test.sh` |

## Related skills

| Skill | When |
|-------|------|
| `daemon-deploy` | SignalR contract / daemon code changed |
| `runtime-deployment` | Build template, provision, smoke test |
| `runtime-debug` | Commands API, logs, env refresh, recovery |
| `self-healing-runtime` | Degraded Online, repair loop, SpecHealth |

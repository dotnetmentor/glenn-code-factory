---
name: self-healing-runtime
description: Self-healing runtime specs — degraded Online boot, SpecHealth, repair loop, and auto-apply consent. Use when (1) shipping daemon changes to bootstrap/spec-health, (2) verifying Online+Degraded path on a broken spec, (3) driving the "Let agent fix it" repair loop, (4) diagnosing wedged Bootstrapping vs correctly Online-degraded, (5) debugging SpecHealth or auto-apply consent.
---

# Self-Healing Runtime Specs

How a runtime reaches **Online** with `SpecHealth = Degraded` when its spec fails to fully apply, how the operator (or agent) repairs it live, and how `SpecHealth` flips `Degraded → Healthy` without a reboot.

> **Paths:** API `:5338`, frontend `:5173`, Postgres per your `DATABASE_URL` / local docker setup.

## Core idea

**Before:** Bootstrap coupled "runtime is Online" with "spec fully applied." A bad install script wedged boot forever.

**Now:**

1. **Decouple alive from spec-applied.** Stages `Install`, `RunningSetup`, `StartingServices` are **non-critical** (`critical: false` in `BootstrapOrchestrator.ts`). Deterministic failure runs once (no 5× retry on spec bugs), records a `BootIssue`, **continues**. Critical stages (`Connecting`, `VerifyEnv`, `Fetching`, `WritingConfig`, `CloningRepo`, `ReportReady`) still fail hard. Transient infra flakes still retry.
2. **Surface it.** Boot issues → `SpecHealth = Degraded` via `ReportSpecHealth` hub method + `SpecDegraded` runtime events. UI shows amber banner: *"Runtime started, but the spec didn't fully apply."*
3. **Let the agent fix it.** **"Let agent fix it"** → `POST /api/runtimes/{id}/repair` → system turn with diagnostic prompt + **budgeted auto-apply consent**. Agent uses `get_runtime_spec`, `get_boot_issues`, `dry_run_install`, `propose_runtime_spec` → auto-applies via delta path → `SpecHealth` flips Healthy, banner clears.

## Data model (`ProjectRuntimes`)

| Column | Meaning |
|--------|---------|
| `SpecHealth` | `Unknown`, `Healthy`, `Degraded` (`RuntimeSpecHealth` enum) |
| `LastBootstrapActivityAt` | Boot-liveness watchdog (silence detection) |
| `AutoApplyNextProposal` | Repair consent armed |
| `AutoApplyExpiresAt` | Consent window expiry |
| `AutoApplyAttemptsRemaining` | Budget remaining (starts at 3) |
| `RepairAttempts` | Loop-guard counter |
| `LastRepairAttemptAt` | Loop-guard timestamp |

Boot-issue **details** live in `RuntimeEvents` (`SpecDegraded`, install/service failure types) + daemon in-memory `BootIssueStore` for `get_boot_issues` tool. No `BootIssuesJson` column on the runtime row.

## Constants (from code)

| Constant | Value | File |
|----------|-------|------|
| `MaxAutoApplyAttempts` | 3 | `RepairRuntimeCommand.cs` |
| `ConsentWindowMinutes` | 30 | `RepairRuntimeCommand.cs` |
| `MaxRepairAttempts` | 5 | `RepairRuntimeCommand.cs` |
| `RepairWindowMinutes` | 60 (sliding) | `RepairRuntimeCommand.cs` |
| `BootstrapSilenceTimeoutMinutes` | 10 | `HeartbeatWatcherJob.cs` |

## Finding a degraded runtime

```bash
psql -c "SELECT \"Id\", \"State\", \"SpecHealth\", \"RepairAttempts\",
                \"AutoApplyAttemptsRemaining\", \"UpdatedAt\"
         FROM \"ProjectRuntimes\"
         ORDER BY \"UpdatedAt\" DESC LIMIT 10;"
```

Look for `State = Online` AND `SpecHealth = Degraded`.

**Status API:** `GET /api/projects/{projectId}/branches/{branchId}/runtime/status` → `RuntimeStatusResponse { specHealth, recentBootIssues[] }`. Boot issues populated only when `specHealth === Degraded`.

## UI flow

**Components:** `RuntimeDegradedBanner` (in `RuntimeStatusHeader`) — used from workspace runtime panel and super-admin `RuntimeDrawer`.

1. Amber banner when `specHealth === 'Degraded'`
2. Expand → boot-issue list (stage / service / reason from `SpecDegraded` events)
3. Click **"Let agent fix it"** → `usePostApiRuntimesRuntimeIdRepair({ runtimeId })`
4. Button shows "Agent is working on it…" while pending
5. Status polls every ~5s; banner clears when `SpecHealth` → Healthy

## API: trigger repair

```bash
curl -s -X POST "http://localhost:5338/api/runtimes/{runtimeId}/repair" \
  -H "Authorization: Bearer <user-jwt>" | jq
```

| Code | Meaning |
|------|---------|
| 200 | Repair turn dispatched + consent armed |
| 404 | Runtime not found or caller lacks access |
| 409 | `repair_attempts_exhausted` (≥5 attempts in 60-min window) |
| 400 | `dispatch_failed` — consent disarmed on failure |

Auth: SuperAdmin, project owner, or workspace member (`RuntimeRepairController`).

## Expected DB state through the loop

| Phase | SpecHealth | AutoApplyNextProposal | AutoApplyAttemptsRemaining | RepairAttempts |
|-------|------------|----------------------|------------------------------|----------------|
| Broken boot | Degraded | false | 0 | 0 |
| After POST /repair | Degraded | **true** | **3** | **+1** |
| Proposal auto-applies but **fails** | Degraded | true | **decremented** | unchanged |
| Proposal auto-applies & **succeeds** | **Healthy** | **false** | **0** | unchanged |

**Invariants:**
- Consent **survives failed applies** (`RecordApplyResultCommand` FAILURE branch)
- SUCCESS clears all consent (one success = healed)
- `CreateRuntimeProposalCommand` auto-runs approve+apply only when consent armed, not expired, budget > 0

## Bootstrap watchdog (why degraded-online works)

`HeartbeatWatcherJob` crashes mid-boot runtimes silent too long. Bootstrap progress emits `RuntimeEvents` but historically didn't bump the runtime row — a long `dotnet restore` looked stale.

**Fix:** `RecordRuntimeEventCommand` best-effort bumps `LastBootstrapActivityAt` on mid-boot events. Watchdog uses `(LastBootstrapActivityAt ?? UpdatedAt) < cutoff` with 10-min silence timeout (must exceed ~5-min silent restore gap).

## Daemon-side flow

After all bootstrap stages, `BootstrapOrchestrator.#finalizeSpecHealth()`:
1. Reads accumulated `BootIssue[]`
2. Calls `signalr.reportSpecHealth({ health: 'Degraded'|'Healthy', issues, summary })`
3. Emits `SpecDegraded` events per issue
4. `ReportReady` still ran → runtime is Online

`ReportSpecHealth` uses raw `invoke()` in `SignalRClient.ts` — **does not require** `generate-signalr.sh` to add the hub method.

## Agent tools for repair

| Tool | Purpose |
|------|---------|
| `get_boot_issues` | Live boot issues from daemon memory |
| `get_runtime_spec` | Current spec JSON |
| `dry_run_install` | Validate install script without applying |
| `propose_runtime_spec` | Submit RuntimeSpecV3 — auto-applies when consent armed |

## Diagnosing failures

| Symptom | Likely cause | Check |
|---------|--------------|-------|
| Wedged Bootstrapping, respawn loop | Watchdog killing mid-boot | `LastBootstrapActivityAt` advancing? Silence timeout 10 min? |
| Online but SpecHealth Unknown | ReportSpecHealth not landing | API logs; daemon `reportSpecHealth`; `rt_runtime` claim resolves RuntimeId server-side |
| Banner missing despite Degraded | Status DTO | `GET .../runtime/status` returns `specHealth` + `recentBootIssues` |
| Repair → 409 | Loop guard | `RepairAttempts >= 5` within 60 min |
| Proposal never auto-applies | Consent/budget | `AutoApplyNextProposal`, expiry, `AutoApplyAttemptsRemaining > 0` |
| Loop stops after first failed fix | Consent cleared wrongly | `RecordApplyResultCommand` FAILURE must leave consent |

## Verify after daemon changes

```bash
npm --prefix packages/daemon install   # if needed
./scripts/generate-signalr.sh          # only if hub contract changed
./scripts/publish-daemon.sh
curl -s http://localhost:5338/api/daemon-versions/resolve | jq '{version,isActive}'
```

Roll bundle onto runtime (force-rebootstrap or recreate), confirm Online+Degraded on intentionally broken spec, then exercise repair loop.

## Related skills

| Skill | When |
|-------|------|
| `runtime-environment` | Full architecture, bootstrap stages, SpecHealth context |
| `daemon-deploy` | Publish daemon after bootstrap/hub changes |
| `runtime-debug` | SSH when boot never reaches Online at all |

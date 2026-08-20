---
name: runtime-deployment
description: End-to-end deployment and verification of project runtimes on Box (box.ascii.dev) — daemon bundle publishing, golden template box building, system settings, runtime fork provisioning, and chat smoke-testing. Use when (1) shipping a new daemon version, (2) building/registering a new golden template box, (3) provisioning a fresh runtime to verify the stack, (4) diagnosing stuck Bootstrapping/Online states, (5) chat events not streaming back, (6) onboarding to the deployment loop.
---

# Runtime Deployment & Self-Verification

End-to-end deployment of project runtimes: daemon bundle publishing, golden
template box, system settings, Box fork provisioning, and chat smoke-testing.

> **Paths:** On a managed platform the repo may live at `/data/project/repo`. Locally, substitute your clone root.

## Mental model

```
packages/daemon/  ──publish-daemon.sh──▶  object storage + DaemonVersions DB
                                              │ resolved at every daemon start via MAIN_API_URL
                                              ▼
scripts/build-box-template.sh ──▶ golden template box (STOPPED = snapshotted)
                                              │ POST /api/admin/runtime-templates (Active)
                       Box API                │
                              ┌───────────────┘  POST /boxes/{template}/fork
                              ▼
              Runtime box → systemd glenn-daemon.service → entrypoint.sh
                              → supervisord → bootstrap-daemon.sh → daemon.js
                              │ SignalR /hubs/runtime
                              ▼
              .NET API → AgentRuntimeBroadcaster → chat events
```

Two independently-shippable artifacts:

| Artifact | Ships when | How | Rollout |
|----------|-----------|-----|---------|
| **Daemon bundle** | daemon code / SignalR contract changes | `./scripts/publish-daemon.sh` (see `daemon-deploy` skill) | auto — bootstrap re-resolves the bundle on every daemon restart; respawn/restart runtimes to force it |
| **Golden template** | system stack changes (Node, postgres, playwright, systemd unit, bootstrap script) | `./scripts/build-box-template.sh` | new forks only — existing runtimes keep their disk; force-recreate (reset-from-scratch) to move one |

## Prerequisites

| What | Where | Verify |
|------|-------|--------|
| Box API key | `SystemSettings` `Box:ApiKey` | `POST /api/admin/box/test-connection` or Super Admin → System Settings → Box → Test |
| Wire assumptions | — | `BOX_API_KEY=... ./scripts/box-smoke-test.sh` — **run on any fresh account / after Box platform changes; it pins every shape BoxClient assumes** |
| Active template | `RuntimeTemplates` table | `GET /api/admin/runtime-templates/latest-active` (404 = build one) |
| Active daemon bundle | `DaemonVersions` | `GET /api/daemon-versions/resolve?channel=stable` |
| Public API URL | `SystemSettings` `Runtime:PublicApiUrl` | daemons dial back here |
| TTL guardrail | `SystemSettings` `Box:DefaultTtlSeconds` | default 21600; never 0 in production |

## Building + shipping a template

```bash
export BOX_API_KEY='...'
# optional auto-registration:
export REGISTER_URL='https://api.yourplatform.example' CI_PUBLISH_KEY='...'
./scripts/build-box-template.sh
```

The script creates a box, provisions the full stack (mirrors the retired
Dockerfile.runtime-base layer-by-layer — the script's own comments are the
authoritative list), installs + enables `glenn-daemon.service`, smoke-checks
that the unit starts, **stops the box** (the stop snapshot IS the template), and
registers it as the newest Active `RuntimeTemplate`. Registering demotes the
previous Active row — new forks use the new template immediately.

Rules:
- Template boxes must **stay stopped**. A running template bills and its disk
  drifts from what was validated (forks take the latest snapshot).
- `BoxAdminController` refuses to delete a registered template box; yank the
  registration first if you truly mean it.
- Debugging a build: `KEEP_RUNNING=1` leaves the box up; stop it manually when done.
- No CI workflow builds templates (needs a live Box account) — it's an operator
  action; `scripts/ci/publish-paths.sh` still classifies the input paths.

## Provisioning a fresh runtime to verify the stack

1. Create a project (or use admin force-respawn / reset-from-scratch on an
   existing one). The provisioner picks up Pending rows within ~60 s (ad-hoc
   enqueue usually within seconds).
2. Watch the state walk: Pending → Booting (fork issued) → Bootstrapping (box
   up, reconciler observed it) → Online (daemon's `RuntimeReady`).
3. `Super Admin → Runtime Monitor` for drift; the runtime drawer's **Box** tab
   shows our-view-vs-Box side by side plus the last 20 `BoxOperation` rows.
4. Send a chat message; confirm events stream back.

Timing expectations: fork → box up in seconds; first bootstrap does the full
clone + install dance (minutes for real projects); resume-wake in a few seconds.

## Stuck-state triage

| Symptom | First look |
|---------|-----------|
| Pending forever | provisioner pre-flight logs: missing `Box:ApiKey` / no Active template / no daemon bundle / start-budget 429 (stays Pending by design — check `api/admin/box/operations` for `rate_limited`/`daily_limit_reached`) |
| Booting forever | box status via drawer Box tab: `provisioning` stuck → Box-side issue; `error` → reconciler will crash+respawn it |
| Bootstrapping forever | daemon never called `RuntimeReady` → `runtime-debug` skill (logs via commands API) |
| Online but degraded banner | `self-healing-runtime` skill |
| Box archived itself unexpectedly | TTL lapsed → is `BoxTtlExtenderJob` running? (Hangfire dashboard) — that's the guardrail doing its job during a control-plane outage |

## After shipping daemon or template

- Daemon: restart runtimes to roll out (`systemctl restart glenn-daemon` via the
  admin commands path, or force-respawn). The bundle is re-resolved at start.
- Template: only new forks get it. To migrate an existing runtime: user-facing
  Reset-from-scratch (abandons disk) or wait for natural attrition.
- Always finish with a chat round-trip on at least one runtime.

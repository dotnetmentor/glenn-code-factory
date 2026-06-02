---
name: runtime-deployment
description: End-to-end deployment and verification of project runtimes — daemon bundle publishing, runtime base image (Docker → Fly registry), system settings, Fly Machine provisioning, and chat smoke-testing. Use when (1) shipping a new daemon version, (2) shipping a new runtime base image, (3) provisioning a fresh runtime to verify the stack, (4) diagnosing stuck Bootstrapping/Online states, (5) chat events not streaming back, (6) onboarding to the deployment loop.
---

# Runtime Deployment & Self-Verification

End-to-end deployment of project runtimes: daemon bundle publishing, runtime base image, system settings, Fly Machine provisioning, and chat smoke-testing.

> **Paths:** On a managed platform the repo may live at `/data/project/repo`. Locally, substitute your clone root.

## Mental model

```
packages/daemon/  ──publish-daemon.sh──▶  object storage + DaemonVersions DB
                                              │
                                              │ DAEMON_BUNDLE_* stamped on machine env
                                              ▼
Dockerfile.runtime-base  ──publish-runtime-image-*──▶  container registry
                                              │
                       Fly Machines API       │
                              ┌───────────────┘
                              ▼
              Fly Machine → supervisord → bootstrap-daemon.sh → daemon.js
                              │ SignalR /hubs/agent
                              ▼
              .NET API → AgentRuntimeBroadcaster → chat events
```

## Agent quickstart (no local Docker)

When the agent container has no Docker socket (common on managed runtimes):

```bash
export FLY_API_TOKEN='FlyV1 …'   # ask operator, or --use-db-token
./scripts/publish-runtime-image-remote.sh
```

This: installs flyctl if needed → remote-builds `Dockerfile.runtime-base` → pushes to your registry → registers + activates via API → prints `TAG=`, `DIGEST=`, `FULL_REF=`.

After it returns: force-recreate affected runtimes (soft-delete + insert `Pending`; provisioner picks up within ~60 s).

**Why not `publish-runtime-image.sh` in the agent?** No docker socket and seccomp blocks rootless builders. `flyctl deploy --remote-only` talks to Fly's hosted builder over HTTPS.

**Agent shell trap:** running the remote publish script in the foreground can die when the shell is torn down mid-build. Use detached execution:

```bash
nohup setsid bash -c 'FLY_API_TOKEN="'"$FLY_API_TOKEN"'" ./scripts/publish-runtime-image-remote.sh; echo EXIT_CODE=$?' \
  > /tmp/runtime-build-run.log 2>&1 < /dev/null &
disown
PUBLISH_PID=$(pgrep -af 'publish-runtime-image-remote.sh' | awk '{print $1}' | head -1)
until ! kill -0 "$PUBLISH_PID" 2>/dev/null; do sleep 20; done
tail -40 /tmp/runtime-build-run.log
```

## Prerequisites

| What | Where | Verify |
|------|-------|--------|
| Fly API token | `SystemSettings` `Fly:ApiToken` | Non-empty |
| Fly app + org | `SystemSettings` `Fly:AppName`, `Fly:OrgSlug` | App exists in org |
| Runtime token signing key | `RuntimeTokens:SigningKeyCurrent` | Auto-generated on first API boot |
| Public API URL | `SystemSettings` `Runtime:PublicApiUrl` | **Reachable from Fly Machines** — use a stable named tunnel or public URL, not ephemeral quick tunnels |
| Object storage creds | env / `FileStorage:R2:*` | Daemon bundles upload here on publish |
| Admin JWT | mint for your environment | Must include `SuperAdmin` for `/api/admin/*` and `publish-daemon.sh` |

> **Do not use ephemeral quick tunnels for `Runtime:PublicApiUrl`.** Shared edge tunnels can split SignalR WebSocket vs HTTP across workers, breaking sticky sessions. Use a **named tunnel** or a stable public hostname.

## Scripts

| Path | Purpose |
|------|---------|
| `scripts/publish-daemon.sh` | Build + bundle + upload daemon + POST `DaemonVersions` |
| `scripts/publish-runtime-image-remote.sh` | Agent path — flyctl remote build → push → register → activate |
| `scripts/publish-runtime-image.sh` | Human/CI path — local docker build → push |
| `Dockerfile.runtime-base` | Minimal substrate; daemon downloaded at boot |

## Ship a new daemon version

See `.claude/skills/daemon-deploy/SKILL.md` for the full SignalR contract checklist. Summary:

```bash
./scripts/generate-signalr.sh    # if hub contract changed
./scripts/publish-daemon.sh
curl -fsS "$API/api/daemon-versions/resolve?channel=stable"
```

Critical invariants: keep `@cursor/sdk` **external** in esbuild; run `publish-daemon.sh` (installs `@cursor/sdk` + sqlite3 native binding for linux-x64).

## Ship a new runtime base image

### Path A — CI / agent / no local Docker (preferred)

GitHub Actions (`.github/workflows/runtime-base-image.yml`) and local CLI both use:

```bash
./scripts/publish-runtime-image-remote.sh
# CI: --no-activate --enforce-size-budget --trivy (then register-runtime-image.sh)
# Local: registers via SuperAdmin JWT unless --no-activate
```

Fly token in CI comes from `GET /api/ci/registry-credentials` (`CONTROL_PLANE_PUBLISH_API_KEY`). Build takes ~5–15 min on Fly's remote builder.

### Path B — Host with Docker

```bash
echo "$FLY_API_TOKEN" | docker login registry.fly.io -u x --password-stdin
./scripts/publish-runtime-image.sh
# Register + activate via super-admin UI or admin API
```

**Registry reference:** store the **full** path including image name (e.g. `registry.fly.io/my-runtime-base`), not host-only. Fly image ref is `{Registry}:{Tag}`.

## Provision a fresh runtime

Provisioner (`RuntimeProvisionerJob`, ~60 s cadence) picks up `Pending` rows:

```bash
curl -fsS -X POST "$API/api/admin/runtimes" \
  -H "Authorization: Bearer $JWT" \
  -H "Content-Type: application/json" \
  -d '{"projectId":"<guid>","branchId":"<guid>","region":"<region>"}'
```

Expected progression (each step < ~30 s):

| State | Meaning |
|-------|---------|
| `Pending` | Row inserted |
| `Booting` | Fly Machine created |
| `Bootstrapping` | Container up, downloading daemon bundle |
| `Online` | Daemon heartbeating |

Stuck > 2 min → see `runtime-debug` skill.

## Chat smoke-test

Connect to `/hubs/agent`, join project group, call `SubmitPrompt` with explicit camelCase object:

```js
await conn.invoke('SubmitPrompt', {
  projectId: PROJECT_ID,
  conversationId: null,
  branchId: BRANCH_ID,
  text: 'Reply with exactly: "Runtime confirmed."',
})
```

Listen for `ReceiveAgentEvent` / `AssistantText`. Exit success = full pipeline works.

## Diagnosis playbook (short)

| Symptom | Likely cause |
|---------|--------------|
| Never leaves `Pending` | Hangfire dead; Fly token empty |
| Stuck in `Booting` | Runtime image not activated; bad digest format |
| Stuck in `Bootstrapping` | Bundle 404/SHA mismatch; API unreachable from machine; hub wire-name desync |
| Briefly `Online` then `Crashed` | Cursor SDK or sqlite3 binding missing in bundle; check `RuntimeErrorReports` |
| SubmitPrompt OK, no reply | Wrong SignalR group; queued session; daemon turn stuck — see `runtime-debug` |
| Two `Active` runtime images | Demote old rows to `Deprecated` via admin API |

## Self-verification checklist

After deployment-affecting changes:

1. `./scripts/publish-daemon.sh` succeeds
2. Fresh `Pending` runtime reaches `Online` within ~90 s
3. Chat round-trip returns assistant text

If all three pass → ship. If any fail → `runtime-debug` before changing more code.

## Anti-patterns

- ❌ Inlining `@cursor/sdk` in esbuild (must stay external; installed at publish time)
- ❌ Ephemeral quick tunnel for `Runtime:PublicApiUrl`
- ❌ Bare `sha256:…` or host-only registry in `RuntimeImages`
- ❌ Positional args to SignalR `SubmitPrompt` — always send the camelCase object
- ❌ Foreground `publish-runtime-image-remote.sh` from an agent shell that may be torn down

## Related skills

| Skill | When |
|-------|------|
| `daemon-deploy` | Hub contract / daemon code changed |
| `runtime-debug` | SSH, logs, hot-swap, OOM, FATAL recovery |
| `runtime-environment` | Full architecture reference |

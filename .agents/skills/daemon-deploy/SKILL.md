---
name: daemon-deploy
description: Rebuild and republish the runtime daemon bundle when the SignalR contract between server and daemon changes. Use when (1) any RuntimeHub C# method is added/renamed/removed, (2) a HubMethodName attribute changes on RuntimeHub, (3) IRuntimeClient receiver methods change, (4) RuntimeHub payload contracts change, (5) a fresh runtime fails bootstrap with "Method does not exist", (6) ./scripts/generate-signalr.sh was run and the daemon was touched, (7) anything in packages/daemon/ changed, (8) user says "publish daemon", "rebuild daemon", or asks why a runtime is stuck in Bootstrapping.
---

# Daemon Bundle Rebuild & Republish

The runtime daemon ships as a **separate downloadable bundle**, not baked into the runtime base image. The base image changes rarely; the daemon bundle can ship in minutes. **They desync if you skip the publish step.**

> **Paths:** On a managed platform the repo may live at `/data/project/repo`. Locally, substitute your clone root.

## The desync trap

A SignalR hub method has two names:

1. **C# method name** on the server (e.g. `GetBootstrap()` or `GetBootstrapAsync()`)
2. **Wire name** — the literal string on the WebSocket frame

| Side | Rule |
|------|------|
| **ASP.NET Core SignalR (server)** | Registers the method with the `Async` suffix **stripped**. `[HubMethodName("X")]` overrides entirely. |
| **TypedSignalR.Client TypeScript (client)** | Emits the **literal** C# method name, including any `Async` suffix. `[HubMethodName("X")]` overrides too. |

If you declare `GetBootstrapAsync()` **without** `[HubMethodName]`:

- Server registers `"GetBootstrap"` (Async stripped).
- Client generates `connection.invoke("GetBootstrapAsync")`.
- Wire mismatch → `HubException: Method does not exist` → bootstrap hangs in `Bootstrapping`.

**Rule:** use bare names — `GetBootstrap`, `RuntimeReady`, `Heartbeat`, etc. Never add an `Async` suffix to hub methods.

`TypedSignalR.Client` bakes wire names into generated TypeScript, then **esbuild freezes them into the published bundle**. Published daemons keep those wire names until you rebuild and republish.

**Symptom:** runtime loops in bootstrap `fetching`. API log shows `Method does not exist` or `Parameters to hub method 'X' are incorrect`.

## When to rebuild (checklist)

Rebuild if **any** of these are true:

- A `RuntimeHub` method was added, renamed, removed, or changed signature
- A `[HubMethodName]` attribute changed on `RuntimeHub`
- `IRuntimeClient` (server → daemon) changed
- Any DTO under `Source/Features/SignalR/Contracts/` shipped to the daemon changed shape
- Anything under `packages/daemon/` was edited
- A new runtime sits in `Bootstrapping` > 2 min with hub method errors in the API log

When in doubt: rebuild. Publish takes ~60 seconds; skipping it causes silent runtime hangs.

## Full sequence

```bash
# 0. Restart the API if hub methods changed (dotnet run does NOT auto-restart on source edits).
#    Skipping this leaves stale server registrations live.

# 1. Regenerate TypedSignalR.Client outputs (server → frontend + daemon)
./scripts/generate-signalr.sh

# 1b. Sanity-check wire names in the generated daemon client
grep -E 'invoke\("(GetBootstrap|RuntimeReady|ReportError|ReportDiskPressure)' \
  packages/daemon/src/generated/signalr/TypedSignalR.Client/index.ts
# Expect: no Async suffix on any of those.

# 2. Rebuild + publish the daemon bundle (esbuild → tar.gz → storage → POST /api/daemon-versions)
./scripts/publish-daemon.sh

# Expected tail:
#   { "version": "2026.5.10-XXXXXX", "channel": "stable", "url": "https://…" }

# 3. Recreate runtimes so they pull the NEW bundle on cold boot.
#    Existing machines keep the old bundle on disk until recreated.
#    Use super-admin "New runtime", or soft-delete + insert Pending via SQL/API.
```

## What publish-daemon.sh enforces

Do not bypass these invariants:

1. **`@cursor/sdk` is `external` in `esbuild.config.mjs`.** Inlining breaks lazy import and native binding resolution at runtime.
2. **`publish-daemon.sh` installs runtime peers** with `--cpu=x64 --os=linux` so platform-specific optional deps resolve on arm64 publish hosts.
3. The script verifies `node_modules/@cursor/sdk` exists and installs the **sqlite3** native binding (required by `@cursor/sdk`) before publishing.

## Auto-activation

`POST /api/daemon-versions` (called by `publish-daemon.sh`) atomically deactivates the previous active row in the same channel and inserts the new row as active. **There is no separate activate step for daemon versions.**

(Runtime **base images** are different — they need a separate super-admin activate step. See `.claude/skills/runtime-deployment/SKILL.md`.)

## Verification

```bash
curl -fsS "$API/api/daemon-versions/resolve?channel=stable" | python3 -m json.tool
# Expect: latest version, isActive: true

tail -f /tmp/api.log | grep -E 'Bootstrap|Hub|HubException|RuntimeReady'
```

Healthy bootstrap: bare wire names invoked, no `Method does not exist`, state progresses `Pending → Booting → Bootstrapping → Online` within ~60–90 s.

If errors persist after publish, the API likely wasn't restarted — see step 0.

## Related skills

| Skill | When |
|-------|------|
| `runtime-deployment` | Runtime base image, Fly provisioning, end-to-end smoke test |
| `runtime-debug` | SSH into a machine, read daemon logs, hot-swap bundle, diagnose OOM/parse errors |

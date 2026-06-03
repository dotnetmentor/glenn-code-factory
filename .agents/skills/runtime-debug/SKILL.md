---
name: runtime-debug
description: Diagnose and fix an agent runtime end-to-end — find the failing Fly Machine, SSH in, read daemon logs, hot-swap the bundle, recover supervisord from FATAL, and verify with a chat round-trip. Use when (1) a runtime is stuck in Bootstrapping/Crashed, (2) a fresh project never goes Online, (3) the daemon boots but chat returns no reply, (4) a daemon change needs verifying on a real machine without a full publish+redeploy cycle.
---

# Runtime Debug

Drive the platform end-to-end: publish a daemon, light up a runtime, send a prompt, and when it breaks — SSH into the Fly Machine, read raw daemon logs, and decide whether to ship a fix or hot-swap to keep moving.

> **Paths:** Substitute your API URL, Fly app name, and Postgres connection for the examples below.

## Mental model

```
.NET API (port 5338)
  │ mints Fly Machines, streams chat over SignalR
  ▼
Fly Machine rt_<runtime-id>
  ├─ tini (PID 1)
  ├─ supervisord
  │   └─ [program:agent] → bootstrap-daemon.sh → node daemon.js
  └─ logs: /var/log/supervisor/agent.{out,err}.log
        ▲
        │ SSH (fly ssh console)
     you + agent-debug
```

Three rules:

1. **The daemon is downloaded at boot.** The base image is empty under `/opt/agent/`. `bootstrap-daemon.sh` curls a tarball from storage keyed by `DAEMON_BUNDLE_URL` + `DAEMON_BUNDLE_SHA256`.
2. **Daemon output is pino JSON** captured by supervisord to `agent.out.log` / `agent.err.log`. Empty files = parse failed before logging.
3. **Runtime state lives in Postgres** (`ProjectRuntimes.State`, `LastHeartbeatAt`). Fresh heartbeat = daemon is alive.

## 30-second triage

### 1. DB — which runtime, what state?

```bash
psql -c "SELECT \"Id\", \"ProjectId\", \"State\", \"FlyMachineId\",
                \"LastHeartbeatAt\", \"CreatedAt\"
         FROM \"ProjectRuntimes\"
         ORDER BY \"CreatedAt\" DESC LIMIT 5;"
```

| State | Meaning |
|-------|---------|
| `Pending` | Not yet picked up by provisioner |
| `Bootstrapping` | Machine up, waiting for first heartbeat |
| `Online` | Heartbeats flowing — healthy |
| `Crashed` | Daemon died (supervisord gave up) |
| `Failed` | Provisioner couldn't create the machine |

`Online` + heartbeat within ~60 s → bug is likely UI, chat, or credentials — not the runtime boot path.

`Bootstrapping` > ~2 min → this skill applies.

### 2. Fly token

Stored encrypted in `SystemSettings` key `Fly:ApiToken`. Ask the operator or use `--use-db-token` on publish scripts.

```bash
export FLY_API_TOKEN='FlyV1 …'
export PATH="$HOME/.fly/bin:$PATH"
fly auth whoami
```

### 3. Machine logs (no SSH yet)

```bash
APP="<your-fly-app-name>"    # from SystemSettings Fly:AppName
MID="<FlyMachineId from DB>"
fly logs -a "$APP" --machine "$MID" | tail -200
```

Look for: `[bootstrap-daemon]` lines, `daemon.bootstrap.*` JSON, `SyntaxError` before any daemon line, bootstrap OK but no daemon JSON (OOM or missing `@cursor/sdk` / sqlite3 binding).

### 4. SSH in

```bash
fly ssh console -a "$APP" -s --machine "$MID"
agent-debug    # baked into the base image
```

`agent-debug` prints env, SHA cache vs env SHA, memory, supervisorctl status, `node --check`, Cursor SDK bundle probe, heartbeat reachability, and recent logs.

## Raw commands (if agent-debug missing)

```bash
/opt/agent/daemon.js                 # downloaded daemon
/opt/agent/.bundle.sha256            # cache marker (hot-swap trap below)
/var/log/supervisor/agent.out.log    # daemon stdout
/var/log/supervisor/agent.err.log    # daemon stderr

node --check /opt/agent/daemon.js
supervisorctl status
supervisorctl restart agent
tail -f /var/log/supervisor/agent.{out,err}.log
free -m                              # OOM check: available < 500 MB is bad
```

**FATAL recovery** (`spawn error` after rapid restarts):

```bash
supervisorctl stop agent && supervisorctl reread && supervisorctl update && supervisorctl start agent
```

## Hot-swap trick

Drop a new `daemon.js` directly without full publish — but **align the SHA cache**:

`bootstrap-daemon.sh` compares `/opt/agent/.bundle.sha256` with `$DAEMON_BUNDLE_SHA256`. Mismatch → wipes `/opt/agent/` and re-downloads the old bundle on restart.

```bash
# Copy new bundle in (fly ssh sftp, etc.)
echo "$SHA_MATCHING_ENV" > /opt/agent/.bundle.sha256
supervisorctl restart agent
tail -f /var/log/supervisor/agent.out.log
```

Hot-swap is for **verification only**. After confirming the fix, run `./scripts/publish-daemon.sh` and update machine env properly.

## Known traps

| Trap | Detection | Fix |
|------|-----------|-----|
| esbuild banner + top-level `import { createRequire }` | `node --check` → `Identifier 'createRequire' has already been declared` | Don't import `createRequire` in `main.ts` — use banner-provided `require` |
| OOM on small machines | `free -m`; daemon crashes on turn start | Machines need ≥ 2 GB RAM |
| supervisord FATAL | `restart agent` → spawn error | stop → reread → update → start |
| SHA cache mismatch | hot-swap reverts on restart | Write matching SHA to `.bundle.sha256` |

## Update machine config via Fly API

To bump memory or roll new daemon env vars without destroying the volume:

```bash
curl -s -H "Authorization: $FLY_API_TOKEN" \
  "https://api.machines.dev/v1/apps/$APP/machines/$MID" > /tmp/m.json

jq --arg url "$NEW_BUNDLE_URL" --arg sha "$NEW_SHA" --arg ver "$NEW_VERSION" \
   '.config.guest.memory_mb = 2048
    | .config.env.DAEMON_BUNDLE_URL = $url
    | .config.env.DAEMON_BUNDLE_SHA256 = $sha
    | .config.env.DAEMON_VERSION = $ver
    | {config}' /tmp/m.json > /tmp/m.patch.json

curl -s -X POST -H "Authorization: $FLY_API_TOKEN" -H "Content-Type: application/json" \
  --data @/tmp/m.patch.json \
  "https://api.machines.dev/v1/apps/$APP/machines/$MID"
```

Poll until state is `started` and `LastHeartbeatAt` is fresh in the DB.

## Full publish + verify

```bash
./scripts/publish-daemon.sh
# Update machine env (above) or recreate runtime via admin UI
# Confirm Online in DB, then drive a chat round-trip in the UI
```

## Quick reference

```
Runtime state?        ProjectRuntimes.State + LastHeartbeatAt
Daemon logs?          /var/log/supervisor/agent.{out,err}.log
Daemon code?          /opt/agent/daemon.js
Which bundle?         $DAEMON_VERSION / $DAEMON_BUNDLE_SHA256 in supervised env
Hot-swap reverting?   env SHA ≠ /opt/agent/.bundle.sha256
Empty logs + restart? node --check /opt/agent/daemon.js
SDK missing?        ls /opt/agent/node_modules/@cursor/sdk
sqlite3 binding?    ls /opt/agent/node_modules/sqlite3/build/Release/node_sqlite3.node
Low memory?         free -m (OOM)
```

## Related skills

| Skill | When |
|-------|------|
| `daemon-deploy` | SignalR contract changed → rebuild + publish bundle |
| `runtime-deployment` | Ship base image, provision runtimes, smoke test |

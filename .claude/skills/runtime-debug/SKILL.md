---
name: runtime-debug
description: Diagnose and fix an agent runtime end-to-end on Box — find the failing box, run commands inside it, read daemon logs, refresh env, hot-swap the bundle, recover from FATAL, and verify with a chat round-trip. Use when (1) a runtime is stuck in Bootstrapping/Crashed, (2) a fresh project never goes Online, (3) the daemon boots but chat returns no reply, (4) a daemon change needs verifying on a real box without a full publish cycle.
---

# Runtime Debug

Drive the platform end-to-end: publish a daemon, light up a runtime, send a
prompt, and when it breaks — reach into the Box VM (commands API or `box ssh`),
read raw daemon logs, and decide whether to ship a fix or hot-swap to keep moving.

> **Paths:** Substitute your API URL and Postgres connection for the examples below.

## Mental model

```
.NET API (port 5338)
  │ forks boxes from the template, streams chat over SignalR
  ▼
Box rt-<runtime-id>              (full Ubuntu VM, systemd)
  ├─ systemd: glenn-daemon.service
  │    └─ entrypoint.sh → supervisord
  │         └─ [program:agent] → bootstrap-daemon.sh → node daemon.js
  ├─ env: per-fork env + /etc/glenn/runtime.env (refresh channel)
  └─ logs: /var/log/supervisor/agent.{out,err}.log
        ▲
        │ POST /boxes/{id}/command   (daemon-independent side channel)
        │ box ssh <id>               (interactive, via the box CLI)
     you + agent-debug
```

Three rules:

1. **The daemon is downloaded at boot.** `/opt/agent/` starts empty;
   `bootstrap-daemon.sh` resolves + curls the bundle via `MAIN_API_URL` on every
   daemon start. A new publish rolls out on the next restart.
2. **Daemon output is pino JSON** captured by supervisord to
   `agent.out.log` / `agent.err.log`. Empty files = it died before logging.
3. **Runtime state lives in Postgres** (`ProjectRuntimes.State`,
   `LastHeartbeatAt`, `BoxId`). Fresh heartbeat = daemon is alive.

## 30-second triage

### 1. DB — which runtime, what state?

```bash
psql -c "SELECT \"Id\", \"ProjectId\", \"State\", \"BoxId\",
                \"LastHeartbeatAt\", \"CreatedAt\"
         FROM \"ProjectRuntimes\"
         ORDER BY \"CreatedAt\" DESC LIMIT 5;"
```

| State | Meaning |
|-------|---------|
| `Pending` | Not yet picked up by provisioner (or start-budget backoff — check BoxOperations for `rate_limited`) |
| `Booting` | Fork/resume issued, box not observed up yet |
| `Bootstrapping` | Box up, waiting for daemon's `RuntimeReady` |
| `Online` | Heartbeats flowing — healthy |
| `Crashed` | Daemon/box died; respawn supervisor takes over |
| `Failed` | Provisioner refused (config/template/mint) — reason in `RuntimeStateEvents` |

`Online` + heartbeat within ~60 s → the bug is likely UI, chat, or credentials — not the boot path.
`Bootstrapping` > ~2 min → this skill applies.

### 2. Box-side truth without leaving the platform

- Runtime drawer → **Box** tab: our-view vs Box status + last 20 `BoxOperation`
  rows (every API call we made, with request/response bodies).
- `GET /api/admin/box/boxes` — all boxes with linkage + orphan flags.
- The audit table directly: `psql -c 'SELECT "Operation","HttpStatusCode","ErrorCode","CreatedAt" FROM "BoxOperations" ORDER BY "CreatedAt" DESC LIMIT 20;'`

### 3. Reach into the box

The commands endpoint is the workhorse (same channel the platform itself uses
for env refresh — it works whenever the box is up, daemon dead or alive):

```bash
BOX_API_BASE_URL=${BOX_API_BASE_URL:-https://ascii.dev/api/box/v1}
run() { curl -fsS -X POST "$BOX_API_BASE_URL/boxes/$BOX_ID/command" \
  -H "Authorization: Bearer $BOX_API_KEY" -H 'Content-Type: application/json' \
  -d "$(python3 -c 'import json,sys;print(json.dumps({"command":sys.argv[1],"timeoutSeconds":120}))' "$1")"; }

run 'systemctl status glenn-daemon --no-pager | head -20'
run 'tail -50 /var/log/supervisor/agent.err.log'
run 'tail -50 /var/log/supervisor/agent.out.log'
run 'supervisorctl -c /etc/supervisor/supervisord.conf status'
run 'cat /etc/glenn/runtime.env | cut -d= -f1'   # keys only — never dump the JWT
```

Interactive alternative (box CLI): `box ssh <box-id>` / `box desktop <box-id>`.
`agent-debug` (installed by the template) wraps the common log/status one-liners.

## Common failures → fixes

| Symptom inside the box | Cause | Fix |
|---|---|---|
| `glenn-daemon.service` inactive | unit crashed / never enabled (template bug) | `run 'sudo systemctl restart glenn-daemon'`; if not enabled → rebuild template |
| bootstrap loops on bundle download | `MAIN_API_URL` wrong/unreachable, or no active daemon version | check `/etc/glenn/runtime.env` keys, `GET /api/daemon-versions/resolve?channel=stable` |
| daemon exits: SignalR 401 | expired/stale runtime JWT (per-fork env is immutable; refresh file missing) | trigger a respawn (mints fresh JWT + writes env file), or manually: server-side `RespawnRuntimeJob`, never hand-craft a token |
| supervisord `FATAL` on a service | spec install didn't run / binary missing | `self-healing-runtime` skill — check BootIssues, repair loop |
| box `archived` while DB says Online | TTL lapsed (extender not running?) or out-of-band stop | reconciler walks it Suspended; wake via UI; check Hangfire `box-ttl-extender` |
| box `error` status | Box-side hardware/VM failure | reconciler → Crashed → respawn reboots it; if persistent, reset-from-scratch (new box) |

## Hot-swap a daemon build (verify a fix without publishing)

```bash
cd packages/daemon && npm run build       # produces dist bundle
# pack + push via the commands API (base64 through commands avoids scp):
tar czf /tmp/daemon-hotswap.tgz -C dist .
B64=$(base64 -w0 /tmp/daemon-hotswap.tgz)
run "echo '$B64' | base64 -d > /tmp/hotswap.tgz && sudo -u agent bash -c 'rm -rf /opt/agent/* && tar xzf /tmp/hotswap.tgz -C /opt/agent' && supervisorctl -c /etc/supervisor/supervisord.conf restart agent"
```

Caveats: a big bundle may exceed the commands payload limit — fall back to
`box scp`. The hot-swap survives daemon restarts but NOT a full
`systemctl restart glenn-daemon` boot cycle if bootstrap re-downloads (it will —
that's by design). Hot-swap is for verification; ship via `publish-daemon.sh`.

## Env refresh by hand (what the platform does on respawn)

```bash
run "sudo tee /etc/glenn/runtime.env >/dev/null <<'EOF'
RUNTIME_ID=...
GLENN_RUNTIME_TOKEN=...
MAIN_API_URL=...
EOF
sudo chmod 600 /etc/glenn/runtime.env && sudo systemctl restart glenn-daemon"
```

Prefer triggering the platform's own respawn — it mints + audits the JWT.

## Verify the fix

1. `psql`: state walks to Online, `LastHeartbeatAt` fresh.
2. Send a chat prompt; watch events stream.
3. If you touched daemon code: publish properly (`daemon-deploy` skill) and
   restart the runtime so the fix comes from the bundle, not the hot-swap.

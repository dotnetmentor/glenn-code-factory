# Runtime Volume Layout — `/data` Contract (v1)

> Backs the `runtime-volume-cache` spec. Every Glenn Machine has a single Fly
> persistent volume mounted at `/data`. **The directory tree below is a contract.**
> Any breaking change requires bumping `LAYOUT_VERSION` in `docker/entrypoint.sh`
> and shipping a migrator under `/opt/agent/migrators/` (deferred to the
> daemon-architecture spec).

---

## 1. Canonical tree

```
/data/
├── .layout-version            ← single line, the version this volume is on (v1 today)
├── .glenn/                  ← platform-owned config + secrets
│   ├── env                    ← decrypted env vars (rewritten atomically by daemon)
│   ├── hooks.json             ← hook config (deferred — daemon-hooks-runner)
│   ├── mcp.json               ← MCP server URLs (deferred — mcp-scoping)
│   └── supervisor.d/          ← drop-in service confs from bootstrap
├── mise/                      ← language toolchain cache, persists across boots
├── project/                   ← user-owned project state
│   ├── repo/                  ← cloned git repository
│   ├── logs/                  ← supervisord stdout/stderr per service
│   └── services/
│       ├── postgres/data/     ← project's local Postgres data dir (pre-created)
│       └── {service-name}/    ← per-service dirs created by the service's install
│                                script — e.g. redis/, minio/, mailhog/, mongodb/
└── agent/                     ← platform-owned, isolated from project tools
    ├── sessions/              ← claude session state, conversation cache
    └── proposals/             ← runtime-curation proposal history
```

The `EnsureVolumeLayout` routine in `docker/entrypoint.sh` creates every directory
above on every boot (idempotent — `mkdir -p`). Files inside are written by their
respective owners (daemon, postgres, etc.).

> **Runtime Spec V2 change:** entrypoint pre-creates **only** `services/postgres/data/`
> because Postgres is the one pre-baked service in the base image. All other services
> (redis, minio, mailhog, mongodb, ...) are installed on demand by their spec's
> `install` script, which is responsible for creating its own `services/{name}/` data
> directory. The entrypoint is intentionally not "smart" about reading the spec —
> that's the daemon's job during bootstrap.

---

## 2. Permissions matrix

The entrypoint runs as user `agent` (uid 1001) per the Dockerfile `USER` directive,
so it can only chmod what it creates. Service-specific ownership (e.g. `postgres`
user owning the postgres data dir) is set by the **service-init step**, not here.

| Path                                       | Owner            | Group  | Mode | Set by              | Purpose                                   |
| ------------------------------------------ | ---------------- | ------ | ---- | ------------------- | ----------------------------------------- |
| `/data`                                    | `agent`          | agent  | 755  | Dockerfile          | volume mount root                         |
| `/data/.layout-version`                    | `agent`          | agent  | 644  | entrypoint          | layout version stamp                      |
| `/data/.glenn`                           | `agent`          | agent  | 700  | entrypoint          | platform secrets folder                   |
| `/data/.glenn/env`                       | `agent`          | agent  | 600  | entrypoint + daemon | decrypted env vars                        |
| `/data/.glenn/supervisor.d`              | `agent`          | agent  | 755  | entrypoint          | per-project supervisord drop-ins          |
| `/data/mise`                               | `agent`          | agent  | 700  | entrypoint          | toolchain cache (warm-boot fast-path)     |
| `/data/project`                            | `agent`          | agent  | 755  | entrypoint          | project root                              |
| `/data/project/repo`                       | `agent`          | agent  | 755  | entrypoint + git    | cloned working tree                       |
| `/data/project/logs`                       | `agent`          | agent  | 755  | entrypoint          | supervisord per-program logs              |
| `/data/project/services`                   | `agent`          | agent  | 755  | entrypoint          | per-service container                     |
| `/data/project/services/postgres/data`     | `postgres`       | postgres | 700 | postgres init       | postgres re-chowns at first init          |
| `/data/project/services/{name}` (others)   | varies           | varies | varies | service install     | created by per-service `install` script   |
| `/data/agent`                              | `agent`          | agent  | 700  | entrypoint          | platform-owned, agent-only                |
| `/data/agent/sessions`                     | `agent`          | agent  | 700  | entrypoint          | Cursor SDK agent session state            |
| `/data/agent/proposals`                    | `agent`          | agent  | 700  | entrypoint          | runtime-curation history                  |

Non-`agent` ownerships in this table (`postgres`) are achieved by the relevant
service's first-run init script — entrypoint creates the directory with mode `700`
and trusts the service-init to chown it before pointing the daemon at it.

---

## 3. Platform-owned vs project-owned

The split exists to defend the agent's audit trail from prompt-injection.

- **`/data/agent/`** — Platform-owned. Only the daemon writes here; project tools
  (the agent's `Bash`, `Edit`, etc.) are file-permission-blocked from touching it.
  This is where claude session state and curation proposals live, and corruption
  here breaks the audit chain — so it's `700` and never exposed to project code.
- **`/data/project/`** — Project-owned. The agent's tools read and write freely
  here. If the user's repo or one of its services nukes itself, that's recoverable.
- **`/data/.glenn/`** — Platform secrets. The daemon writes `env` atomically
  whenever main API rotates secrets; project code reads but never writes.

---

## 4. Layout versioning

`/data/.layout-version` holds a single integer — the schema version this volume
was last reconciled against.

- The entrypoint creates the file with the current `LAYOUT_VERSION` (today: `1`)
  on first boot, and **never overwrites it after that**.
- When we change the layout (e.g. v2 splits services into per-engine subdirs),
  we ship a migrator under `/opt/agent/migrators/002-*.sh` and bump
  `LAYOUT_VERSION` in entrypoint.sh.
- On boot the daemon (deferred to daemon-architecture) reads the file, finds
  every migrator with target > current value, runs them in order, and writes
  the new version atomically.

Migrators must be idempotent: they may run twice if a boot is interrupted.

---

## 5. Fast-path detection (warm boots)

Bootstrap (deferred to `runtime-bootstrap` spec) skips expensive steps when it
sees pre-existing state:

| Condition                              | Skip                              |
| -------------------------------------- | --------------------------------- |
| `/data/project/repo/.git` exists       | initial `git clone`               |
| `/data/mise/installs/` non-empty       | bulk `mise install`               |
| `/data/.glenn/env` size > 0          | env-decrypt round-trip (refresh)  |
| `/data/project/services/postgres/data/PG_VERSION` exists | `initdb`        |

A warm boot is "wake-to-ready in ~2 s" by hitting all of these.

---

## 6. Secure deletion

- Volumes are encrypted at rest by Fly default (`Encrypted = true` on every
  `CreateVolumeRequest`).
- When a project is destroyed, main API calls
  `DELETE /api/admin/fly/volumes/{id}` (FlyAdminController) → Fly performs
  cryptographic erasure of the encryption key.
- Best-effort scrub before destroy: the daemon overwrites `/data/.glenn/env`
  with zeros so any post-mortem disk-recovery sees an empty file. Volume
  encryption is the real guarantee — this is belt-and-braces.
- The `FlyOperation` audit row records the destroy with operator = "system",
  volume id, and timestamp. That row is **append-only** (no soft-delete on
  `FlyOperation`), so the trail survives even if the project's other tables
  are purged.

---

## 7. Disk-pressure thresholds

Three watermarks emitted by the daemon's disk monitor (deferred to
daemon-architecture):

| Used %  | Event                | UI behaviour                                   |
| ------- | -------------------- | ---------------------------------------------- |
| ≥ 80 %  | `disk_pressure:warn` | Banner: "Running low on disk"                  |
| ≥ 90 %  | `disk_pressure:high` | Modal: "Upgrade to N GB?"                      |
| ≥ 95 %  | `disk_pressure:crit` | Disable writes from agent tools; offer rescue  |

The monitor uses hysteresis: each threshold fires at most once per crossing
upward. Crossing back below resets the latch.

When the user accepts an upgrade the UI POSTs to
`/api/admin/fly/volumes/{id}/extend` — see `FlyAdminController.ExtendVolume`.
On success the daemon detects the new size via `df` and emits
`disk_capacity_changed` so the UI can dismiss its banner without a refresh.

---

## 8. Volume sizing

- **Default new volume: 5 GB** — bumped from 1 GB after we observed `npm install`
  failing with `ENOSPC: no space left on device` on monorepo projects despite
  bytes still being free. Cause: Fly auto-formats volumes as ext4 with the default
  bytes-per-inode of 16 KiB, so a 1 GB volume gets only ~64k inodes — exhausted
  by a single `@mui/icons-material` (~8.6k tiny `.d.ts` files) plus the rest of a
  React monorepo. 5 GB ≈ ~320k inodes, comfortably covers a typical install.
  Provisioning happens via `FlyClient.CreateVolumeAsync` with the project's region;
  the default itself lives on `ProjectRuntime.VolumeSizeGb`.
- **Online extension** — `FlyClient.ExtendVolumeAsync` (= PUT
  `/v1/apps/{app}/volumes/{id}/extend`). No reboot required; the kernel sees
  the new size on the next `df`. Inodes scale with the new size since ext4
  resize re-tunes the inode table.
- **Shrinking is unsupported** — Fly rejects 422 on shrink attempts; the
  controller does not re-validate.

---

## 9. Layout-v1 freeze

This document defines **Layout v1**. Going forward:

- **Adding a new top-level subdir** is non-breaking only if no existing tool
  reads `/data` as a flat list. Today nothing does, so additions are safe and
  do not require a version bump.
- **Renaming, moving, or removing** a path in this document is breaking and
  requires v2 + a migrator.
- **Changing permissions** in the matrix is breaking if it tightens what
  another component already relies on.

When in doubt: add a new subdir, leave existing ones untouched.

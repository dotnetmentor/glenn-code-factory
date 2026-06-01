#!/usr/bin/env bash
# =====================================================================================
# Glenn Runtime Base — Entrypoint
# =====================================================================================
# Idempotent: prepares the canonical /data layout (see runtime-volume-cache spec) on
# every boot, then exec's the CMD (typically supervisord). Safe to re-run on a Machine
# whose volume already has data — no clobbering.
#
# Runs as user `agent` (uid 1001) per Dockerfile USER directive. We can chmod what we
# create, but cannot chown to other users (e.g. postgres). Service-specific ownership
# (postgres data dir → postgres user) is handled at service-init time, not here.
#
# Volume layout: a contract. Any change is breaking and requires a migrator.
# Current layout version: 1.
# =====================================================================================
set -euo pipefail

LAYOUT_VERSION=1

# ------------------------------------------------------------------------------------
# Apply kernel sysctl values baked into /etc/sysctl.d/ (inotify limits etc.). We
# don't run systemd, so this has to be done explicitly. The `agent` user has
# passwordless sudo (Dockerfile Layer 5). Best-effort: in some local-dev envs
# /proc/sys is read-only — don't fail the boot.
# ------------------------------------------------------------------------------------
if command -v sudo >/dev/null 2>&1; then
    sudo sysctl --system >/dev/null 2>&1 || true
fi

# ------------------------------------------------------------------------------------
# Graceful degradation: if /data isn't a real volume mount (e.g. ad-hoc `docker run`
# without -v), we still create the layout so the container boots cleanly. The volume
# encryption + persistence guarantees only kick in when Fly attaches a real volume.
# ------------------------------------------------------------------------------------
if [[ ! -d /data ]]; then
    mkdir -p /data
fi

# ------------------------------------------------------------------------------------
# Canonical layout — see runtime-volume-cache spec.
#
# Permissions matrix (this entrypoint enforces what it can; the rest are service-init):
#   /data/mise                                  agent:agent  700  toolchain cache
#   /data/project/repo                          agent:agent  755  git working tree
#   /data/project/services                      agent:agent  755  service container
#   /data/project/services/postgres/data        postgres:*   700  postgres init re-chowns
#   /data/project/logs                          agent:agent  755  supervisord stdout/err
#   /data/.glenn                              agent:agent  700  platform secrets dir
#   /data/.glenn/env                          agent:agent  600  decrypted env vars
#   /data/.glenn/supervisor.d                 agent:agent  755  drop-in service confs
#   /data/agent/sessions                        agent:agent  700  claude session state
#   /data/agent/proposals                       agent:agent  700  curation proposals
#
# Pre-V2 we also pre-created /data/project/services/{redis,minio,mailhog}. We no
# longer do — those services (and any others a project adds) install on demand via
# their spec's `install` script, which is responsible for creating its own data
# directory under /data/project/services/{name}/. Postgres stays as the lone special
# case here because its binary IS pre-baked in the image (see Dockerfile Layer 1c)
# and its data dir is the warm-boot fast-path signal (`PG_VERSION` file existence
# tells bootstrap to skip `initdb`).
# ------------------------------------------------------------------------------------

# 0755 dirs — readable for tooling, writable only by agent
mkdir -p \
    /data/project \
    /data/project/repo \
    /data/project/services \
    /data/project/logs \
    /data/.glenn/supervisor.d

# 0700 dirs — agent-only (caches, secrets, session state)
mkdir -p \
    /data/mise \
    /data/.glenn \
    /data/agent \
    /data/agent/sessions \
    /data/agent/proposals \
    /data/project/services/postgres/data

# chmod_safe — Always-succeeds chmod for paths that may be foreign-owned.
#
# Background: a previous boot's runtime spec install snippet may have run
# `sudo chown -R <svc-user>:<svc-user> <path>` on a directory under /data.
# When this entrypoint re-runs as `agent` on a subsequent boot, a bare
# `chmod` on a now-foreign-owned dir fails with `Operation not permitted`
# and (with `set -e` upstream) wedges the whole runtime in Failed forever.
#
# Strategy: try sudo first (agent has NOPASSWD on the runtime base image),
# fall back to direct chmod for environments without sudo (local dev), and
# never propagate failure — a chmod miss on one cache dir is not worth
# blocking the boot. The cost of the extra sudo invocation on a normal
# boot is sub-millisecond.
chmod_safe() {
    local mode="$1"
    local path="$2"
    if command -v sudo >/dev/null 2>&1; then
        sudo chmod "$mode" "$path" 2>/dev/null && return 0
    fi
    chmod "$mode" "$path" 2>/dev/null || true
}

chmod_safe 700 /data/mise
chmod_safe 700 /data/.glenn
chmod_safe 700 /data/agent
chmod_safe 700 /data/agent/sessions
chmod_safe 700 /data/agent/proposals
chmod_safe 700 /data/project/services/postgres/data

# Try to chown the postgres data dir to postgres:postgres. This will succeed only when
# entrypoint runs as root (e.g. local docker run without a USER override during a one-
# off init). When entrypoint runs as `agent` (the default per Dockerfile USER), the
# chown is a no-op silently — postgres' own service-init (`initdb`) handles the chown
# on first boot. Either path leaves the dir correctly owned before postgres needs it.
if [[ "$(id -u)" == "0" ]] && getent passwd postgres >/dev/null 2>&1; then
    chown postgres:postgres /data/project/services/postgres/data || true
fi

# Decrypted env vars — daemon overwrites atomically when secrets rotate.
if [[ ! -f /data/.glenn/env ]]; then
    touch /data/.glenn/env 2>/dev/null || sudo touch /data/.glenn/env 2>/dev/null || true
fi
chmod_safe 600 /data/.glenn/env

# ------------------------------------------------------------------------------------
# Layout version stamp. Migrators (deferred to daemon-architecture spec) read this on
# boot and run any /opt/agent/migrators/NNN-*.sh whose target version is greater than
# the value here, then bump it. We write the file once on first boot and never touch
# it again at this level.
# ------------------------------------------------------------------------------------
if [[ ! -f /data/.layout-version ]]; then
    echo "$LAYOUT_VERSION" > /data/.layout-version 2>/dev/null \
        || sudo bash -c "echo $LAYOUT_VERSION > /data/.layout-version" 2>/dev/null \
        || true
    chmod_safe 644 /data/.layout-version
fi

# ------------------------------------------------------------------------------------
# Tool cache + PATH. mise stores its toolchains under /data/mise so they survive
# volume re-attach onto a fresh Machine.
# ------------------------------------------------------------------------------------
export MISE_DATA_DIR=/data/mise
export PATH="/data/mise/shims:${PATH}"

# ------------------------------------------------------------------------------------
# Docker socket GID alignment.
#
# The base image bakes `docker` group at GID 999 and adds `agent` to it (see
# Dockerfile.runtime-base Layer 5). When /var/run/docker.sock is exposed to
# this container — either bind-mounted from the host or created by a sibling
# dockerd — its GID is whatever the *runtime* host uses, which may not be 999.
#
# Membership is resolved by GID, not group name: if the socket says GID 117
# but our in-image `docker` group is at 999, agent's `id` lists 999 in its
# supplementary groups and the kernel still denies access to the socket.
#
# Fix at boot:
#   1. Read the socket's GID.
#   2. If it differs from the in-image `docker` group's GID, `groupmod` the
#      group to match. agent's membership now resolves to the correct GID by
#      name → GID lookup.
#   3. Group memberships of an already-running process are FROZEN at process
#      creation, so we must re-exec the rest of the boot under `sg docker -c`
#      to load the new GID into the supplementary group list. supervisord and
#      every service it forks then inherit it.
#
# Guarded by a marker env var to avoid a re-exec loop if `sg` itself triggers
# entrypoint again somehow. No-op when the socket isn't present (most local
# dev) — agents that don't need Docker pay zero cost.
# ------------------------------------------------------------------------------------
if [[ -z "${_DOCKER_GID_ALIGNED:-}" ]] \
        && [[ -S /var/run/docker.sock ]] \
        && command -v sudo >/dev/null 2>&1 \
        && command -v sg >/dev/null 2>&1; then
    SOCK_GID=$(stat -c '%g' /var/run/docker.sock 2>/dev/null || echo "")
    CURRENT_DOCKER_GID=$(getent group docker | cut -d: -f3 || echo "")
    if [[ -n "$SOCK_GID" && "$SOCK_GID" != "$CURRENT_DOCKER_GID" ]]; then
        sudo groupmod -g "$SOCK_GID" docker 2>/dev/null || true
    fi
    export _DOCKER_GID_ALIGNED=1
    # `sg docker -c "..."` opens a new shell with `docker` in the effective
    # group list, then exec's the quoted command. We pass the original CMD
    # ("$*") through. Quoting note: CMD args in our setup are simple
    # ("supervisord -n -c /etc/supervisor/supervisord.conf") — no embedded
    # spaces or shell metacharacters that would need careful re-quoting.
    exec sg docker -c "$*"
fi

# Hand off to supervisord (or whatever CMD was passed)
exec "$@"

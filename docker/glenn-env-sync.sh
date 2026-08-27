#!/usr/bin/env bash
# glenn-env-sync — materialize Box's per-fork env for systemd.
#
# Box delivers per-fork (and per-resume) env to command/agent processes via
# /run/ascii-secrets/env.sh (`export KEY=value` lines, root-readable) and does
# NOT write /etc/environment, so glenn-daemon.service can never see fork-time
# identity (RUNTIME_ID, GLENN_RUNTIME_TOKEN, MAIN_API_URL, ...) on its own —
# verified by box-smoke-test.sh item 10. This shim runs as root
# (ExecStartPre=+) before every daemon start and converts that file into
# systemd EnvironmentFile syntax at /etc/glenn/box-env.env.
#
# Layering (the unit reads env files top-down; LATER files win per key):
#   /etc/environment        — legacy fallback, effectively empty on Box
#   /etc/glenn/box-env.env  — this shim's output (fork/resume-time identity)
#   /etc/glenn/runtime.env  — the platform's refresh channel (fresh JWTs on
#                             respawn) — deliberately last so it wins.
set -euo pipefail

SRC=/run/ascii-secrets/env.sh
OUT=/etc/glenn/box-env.env

mkdir -p /etc/glenn

# ---------------------------------------------------------------------------
# Boot-path self-healing (2026-08-26 finding, pinned by a marker experiment):
# Box's snapshot/restore preserves only a subset of the filesystem — /opt,
# /etc, /usr, /var and /home/user survive stop/resume and ride into forks;
# NEW root-level dirs, /root, and other /home/<user> dirs are silently
# DROPPED. Durable state therefore lives under /opt/glenn/** and /data +
# /home/agent are symlinks into it — but the symlinks themselves are
# root/home-level entries that restores drop. Without /data supervisord dies
# on chdir (agent FATAL, unit looks healthy); without /home/agent the Cursor
# SDK store mkdir EACCESes mid-turn. The unit runs as agent and can create
# neither, so this root ExecStartPre re-links them on every start.
#
# Legacy boxes (pre-symlink templates) may still have a REAL /data or
# /home/agent directory — the -e guards leave those untouched; their content
# is live and moves to /opt/glenn only via an explicit migration.
# ---------------------------------------------------------------------------
mkdir -p /opt/glenn/data /opt/glenn/agent-home
if [[ ! -e /home/agent ]]; then
    cp -rT /etc/skel /opt/glenn/agent-home 2>/dev/null || true
    ln -sfn /opt/glenn/agent-home /home/agent
fi
chown agent:agent /opt/glenn /opt/glenn/data /opt/glenn/agent-home
if [[ ! -e /data ]]; then
    ln -sfn /opt/glenn/data /data
fi

# The box agent writes env.sh during VM boot and may race us on a cold start.
# Wait briefly; if it never appears (the template box itself has no per-fork
# env) write an empty file and let the daemon proceed — Restart=always re-runs
# this shim on every daemon start, so a late-arriving env.sh is picked up.
for _ in $(seq 1 30); do
    [[ -s "$SRC" ]] && break
    sleep 1
done

if [[ ! -s "$SRC" ]]; then
    : > "$OUT"
    chown agent:agent "$OUT"
    chmod 600 "$OUT"
    exit 0
fi

# Source in a clean environment and dump the resulting variables as KEY=value
# lines systemd can parse (values are single-line: JWTs, URLs, hostnames).
# Exclude the shell's own bookkeeping variables.
env -i bash -c "set -a; . '$SRC' >/dev/null 2>&1; env" \
    | grep -vE '^(PWD|SHLVL|PATH|HOME|_)=' > "$OUT.tmp"
chown agent:agent "$OUT.tmp"
chmod 600 "$OUT.tmp"
mv -f "$OUT.tmp" "$OUT"

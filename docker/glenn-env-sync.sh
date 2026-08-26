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
# Boot-path self-healing (2026-08-26 finding): Box's snapshot/restore does NOT
# bring back every path — /data and /home/agent were observed missing after
# stop/resume AND on fresh forks of a template that verifiably had both, while
# /opt and /etc content from the same build survived. Without /data supervisord
# dies on chdir (agent FATAL); without /home/agent the Cursor SDK store mkdir
# EACCESes mid-turn. The unit runs as agent and cannot create either, so this
# root ExecStartPre is the one place that can guarantee them on every start.
# Idempotent: no-ops when the dirs already exist with content.
# ---------------------------------------------------------------------------
if [[ ! -d /data ]]; then
    mkdir -p /data
    chown agent:agent /data
fi
if [[ ! -d /home/agent ]]; then
    mkdir -p /home/agent
    cp -rT /etc/skel /home/agent 2>/dev/null || true
    chown -R agent:agent /home/agent
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

#!/usr/bin/env bash
# =====================================================================================
# Build the golden template box (Box-native successor to publish-runtime-image.sh)
# =====================================================================================
# Creates a fresh box on the Box account (box.ascii.dev), provisions it with the full
# runtime stack that Dockerfile.runtime-base used to bake into an OCI image, stops it
# (Box snapshots the disk on stop — the snapshot IS the template), and optionally
# registers it as the platform's active RuntimeTemplate.
#
# Every project runtime is then a FORK of this box: it inherits the entire prepared
# disk and receives its identity via per-fork env vars. See
# .claude/skills/runtime-deployment/SKILL.md for the full pipeline.
#
# Stack provisioned (mirrors the old base image 1:1 unless noted):
#   - OS packages: git, build-essential, python3, supervisor, gh, sudo, procps, ...
#   - PostgreSQL (distro default) — the one pre-baked service
#   - cloudflared (pinned .deb) — preview tunnel client
#   - Node 20 (nodesource) + mise (pinned)
#   - Playwright + Chromium (PLAYWRIGHT_BROWSERS_PATH=/opt/playwright-browsers)
#   - agent user (uid 1001) + passwordless sudo + docker group
#   - inotify sysctl bumps (systemd applies /etc/sysctl.d natively — no entrypoint hack)
#   - /usr/local/bin/{bootstrap-daemon.sh, entrypoint.sh, agent-debug} from docker/
#   - /etc/supervisor/supervisord.conf from docker/supervisord.base.conf
#   - systemd unit `glenn-daemon.service`: runs entrypoint.sh + supervisord as agent,
#     EnvironmentFile=/etc/glenn/runtime.env (+ /etc/environment as fallback for
#     Box-injected per-fork env). Enabled ⇒ survives stop/resume/fork — this is the
#     property the whole reboot/respawn design leans on.
#
# NOT baked (unchanged philosophy): the daemon bundle (downloaded at boot from
# DAEMON_BUNDLE_URL / resolved via MAIN_API_URL), redis/minio/etc (spec installs).
#
# Requirements:
#   BOX_API_KEY        Box API key (create with `box api-key create`)
# Optional:
#   BOX_API_BASE_URL   default https://api.ascii.dev/v1
#   BOX_SIZE           template box size (default: small — forks can pick their own)
#   REGISTER_URL       platform API base (e.g. https://api.glenncode.ai) — when set
#                      together with CI_PUBLISH_KEY, registers the template
#   CI_PUBLISH_KEY     publish API key for POST /api/admin/runtime-templates
#   KEEP_RUNNING=1     skip the final stop (debugging the provision interactively)
#
# NOTE (first-run verification): the exact Box wire shapes (create body, commands
# endpoint response, stop semantics) are pinned by scripts/box-smoke-test.sh — run
# that FIRST on a fresh account and fix any drift here before trusting this script.
# =====================================================================================
set -euo pipefail

BOX_API_BASE_URL="${BOX_API_BASE_URL:-https://api.ascii.dev/v1}"
BOX_SIZE="${BOX_SIZE:-small}"
CLOUDFLARED_VERSION="${CLOUDFLARED_VERSION:-2026.5.2}"
MISE_VERSION="${MISE_VERSION:-v2025.1.5}"
NODE_MAJOR="${NODE_MAJOR:-20}"

: "${BOX_API_KEY:?BOX_API_KEY is required (box api-key create)}"

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
GIT_SHA="$(git -C "$REPO_ROOT" rev-parse --short HEAD 2>/dev/null || echo unknown)"
LABEL="base-$(date -u +%Y.%m.%d)-${GIT_SHA}"

api() { # api METHOD PATH [JSON_BODY]
    local method="$1" path="$2" body="${3:-}"
    if [[ -n "$body" ]]; then
        curl -fsS -X "$method" "$BOX_API_BASE_URL$path" \
            -H "Authorization: Bearer $BOX_API_KEY" \
            -H "Content-Type: application/json" \
            -d "$body"
    else
        curl -fsS -X "$method" "$BOX_API_BASE_URL$path" \
            -H "Authorization: Bearer $BOX_API_KEY"
    fi
}

# Run a shell command inside the box, failing loudly on non-zero exit.
box_exec() { # box_exec BOX_ID DESCRIPTION COMMAND
    local box_id="$1" desc="$2" cmd="$3"
    echo "  → $desc"
    local payload result exit_code
    payload=$(python3 -c 'import json,sys; print(json.dumps({"command": sys.argv[1]}))' "$cmd")
    result=$(api POST "/boxes/$box_id/commands" "$payload")
    exit_code=$(echo "$result" | python3 -c 'import json,sys; d=json.load(sys.stdin); print(d.get("exitCode", d.get("exit_code", 0)) or 0)' 2>/dev/null || echo 0)
    if [[ "$exit_code" != "0" ]]; then
        echo "❌ step failed (exit $exit_code): $desc"
        echo "$result" | head -c 4000
        exit 1
    fi
}

# Push a local file into the box via base64 through the commands endpoint (avoids
# needing the files API shape to be verified; commands is the one endpoint the
# whole platform already depends on).
box_put_file() { # box_put_file BOX_ID LOCAL_PATH REMOTE_PATH MODE
    local box_id="$1" local_path="$2" remote_path="$3" mode="$4"
    local b64
    b64=$(base64 -w0 "$local_path")
    box_exec "$box_id" "install $(basename "$local_path") → $remote_path" \
        "sudo mkdir -p $(dirname "$remote_path") && echo '$b64' | base64 -d | sudo tee $remote_path >/dev/null && sudo chmod $mode $remote_path"
}

wait_for_status() { # wait_for_status BOX_ID WANTED_PREDICATE_PYTHON
    local box_id="$1" want="$2" status
    for _ in $(seq 1 60); do
        status=$(api GET "/boxes/$box_id" | python3 -c 'import json,sys
d=json.load(sys.stdin)
d=d.get("box", d)
print(d.get("status",""))')
        if python3 -c "import sys; sys.exit(0 if ('$status' $want) else 1)"; then
            echo "$status"
            return 0
        fi
        sleep 3
    done
    echo "❌ box $box_id never reached wanted status (last: $status)" >&2
    return 1
}

echo "📦 Creating template box ($BOX_SIZE) ..."
CREATE_BODY=$(python3 -c 'import json,sys; print(json.dumps({"name": sys.argv[1], "size": sys.argv[2]}))' "template-$LABEL" "$BOX_SIZE")
BOX_ID=$(api POST "/boxes" "$CREATE_BODY" | python3 -c 'import json,sys
d=json.load(sys.stdin)
d=d.get("box", d)
print(d["id"])')
echo "   box id: $BOX_ID"

echo "⏳ Waiting for the box to come up ..."
wait_for_status "$BOX_ID" "in ('ready','idle','running')" >/dev/null
echo "✅ box is up"

echo "🔧 Provisioning (this mirrors Dockerfile.runtime-base layer by layer) ..."

box_exec "$BOX_ID" "OS packages" \
    "sudo DEBIAN_FRONTEND=noninteractive apt-get update -q && sudo DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends bash ca-certificates curl wget gnupg git build-essential supervisor python3 procps tar gzip sudo"

box_exec "$BOX_ID" "inotify sysctl bumps" \
    "printf 'fs.inotify.max_user_watches=524288\nfs.inotify.max_user_instances=512\n' | sudo tee /etc/sysctl.d/90-inotify.conf >/dev/null && sudo sysctl --system >/dev/null"

box_exec "$BOX_ID" "system git identity" \
    "sudo git config --system user.name 'Glenncode Agent' && sudo git config --system user.email 'agent@glenncode.ai'"

box_exec "$BOX_ID" "PostgreSQL (distro default) + contrib + client" \
    "sudo DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends postgresql postgresql-contrib postgresql-client && pg_config --version && sudo systemctl disable --now postgresql || true"
# ^ the distro's own postgresql unit is disabled: our supervisord fragment owns the
#   server, pointed at /data/project/services/postgres/data — same as the Fly image.

box_exec "$BOX_ID" "cloudflared $CLOUDFLARED_VERSION" \
    "curl -fsSL -o /tmp/cloudflared.deb https://github.com/cloudflare/cloudflared/releases/download/$CLOUDFLARED_VERSION/cloudflared-linux-amd64.deb && sudo dpkg -i /tmp/cloudflared.deb && rm /tmp/cloudflared.deb && cloudflared --version && sudo systemctl disable --now cloudflared 2>/dev/null || true"

box_exec "$BOX_ID" "Node $NODE_MAJOR (nodesource)" \
    "curl -fsSL https://deb.nodesource.com/setup_${NODE_MAJOR}.x | sudo bash - && sudo apt-get install -y --no-install-recommends nodejs && node --version && npm --version"

box_exec "$BOX_ID" "GitHub CLI" \
    "curl -fsSL https://cli.github.com/packages/githubcli-archive-keyring.gpg | sudo dd of=/usr/share/keyrings/githubcli-archive-keyring.gpg 2>/dev/null && sudo chmod 0644 /usr/share/keyrings/githubcli-archive-keyring.gpg && echo \"deb [arch=\$(dpkg --print-architecture) signed-by=/usr/share/keyrings/githubcli-archive-keyring.gpg] https://cli.github.com/packages stable main\" | sudo tee /etc/apt/sources.list.d/github-cli.list >/dev/null && sudo apt-get update -q && sudo apt-get install -y --no-install-recommends gh"

box_exec "$BOX_ID" "mise $MISE_VERSION" \
    "curl -fsSL https://mise.run | sudo MISE_VERSION=$MISE_VERSION MISE_INSTALL_PATH=/usr/local/bin/mise sh && mise --version"

box_exec "$BOX_ID" "agent user (uid 1001) + sudoers + docker group" \
    "id -u agent >/dev/null 2>&1 || sudo useradd --create-home --shell /bin/bash --uid 1001 agent; sudo mkdir -p /data /opt/agent /var/log/supervisor /etc/supervisor/conf.d /etc/glenn && sudo chown -R agent:agent /data /opt/agent /var/log/supervisor /etc/supervisor/conf.d && echo 'agent ALL=(ALL) NOPASSWD: ALL' | sudo tee /etc/sudoers.d/agent >/dev/null && sudo chmod 0440 /etc/sudoers.d/agent && sudo visudo -c -f /etc/sudoers.d/agent && (getent group docker >/dev/null 2>&1 || sudo groupadd docker) && sudo usermod -aG docker agent"

box_exec "$BOX_ID" "Playwright + Chromium (system-wide)" \
    "sudo mkdir -p /opt/playwright-browsers && sudo chown -R agent:agent /opt/playwright-browsers && sudo npm install -g playwright@latest && sudo PLAYWRIGHT_BROWSERS_PATH=/opt/playwright-browsers npx playwright install --with-deps chromium && sudo npm cache clean --force && echo 'PLAYWRIGHT_BROWSERS_PATH=/opt/playwright-browsers' | sudo tee -a /etc/environment >/dev/null"

echo "📄 Installing platform scripts + supervisord config ..."
box_put_file "$BOX_ID" "$REPO_ROOT/docker/bootstrap-daemon.sh"    /usr/local/bin/bootstrap-daemon.sh 755
box_put_file "$BOX_ID" "$REPO_ROOT/docker/entrypoint.sh"          /usr/local/bin/entrypoint.sh       755
box_put_file "$BOX_ID" "$REPO_ROOT/docker/agent-debug.sh"         /usr/local/bin/agent-debug         755
box_put_file "$BOX_ID" "$REPO_ROOT/docker/snap-preview.cjs"       /usr/local/bin/snap-preview.cjs    644
box_put_file "$BOX_ID" "$REPO_ROOT/docker/snap-preview"           /usr/local/bin/snap-preview        755
box_put_file "$BOX_ID" "$REPO_ROOT/docker/supervisord.base.conf"  /etc/supervisor/supervisord.conf   644

echo "⚙️  Installing the glenn-daemon systemd unit ..."
# The unit is the load-bearing piece: enabled systemd services survive Box's
# stop/resume/fork, so a forked runtime box boots supervisord + the daemon with no
# outside help. Env layering, first match wins per key (systemd reads top-down):
#   /etc/glenn/runtime.env  — the platform's refresh channel (provisioner/respawn
#                             write it via the commands API; fresh JWTs land here)
#   /etc/environment        — Box-injected account/per-fork env (spike-verified
#                             delivery channel; harmless if empty)
# Both files are optional (`-` prefix) so a template box with neither still boots.
UNIT_B64=$(base64 -w0 <<'UNIT'
[Unit]
Description=Glenn runtime (supervisord + agent daemon)
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
User=agent
EnvironmentFile=-/etc/environment
EnvironmentFile=-/etc/glenn/runtime.env
Environment=NODE_ENV=production
Environment=MISE_DATA_DIR=/data/mise
Environment=PLAYWRIGHT_BROWSERS_PATH=/opt/playwright-browsers
ExecStart=/usr/local/bin/entrypoint.sh supervisord -n -c /etc/supervisor/supervisord.conf
Restart=always
RestartSec=3
KillMode=mixed
TimeoutStopSec=20

[Install]
WantedBy=multi-user.target
UNIT
)
box_exec "$BOX_ID" "write + enable glenn-daemon.service" \
    "echo '$UNIT_B64' | base64 -d | sudo tee /etc/systemd/system/glenn-daemon.service >/dev/null && sudo touch /etc/glenn/runtime.env && sudo chown agent:agent /etc/glenn/runtime.env && sudo chmod 600 /etc/glenn/runtime.env && sudo systemctl daemon-reload && sudo systemctl enable glenn-daemon.service"

# Sanity: the unit must START (the daemon inside will fail to fetch its bundle
# without env — that's expected on the template; supervisord itself must come up).
box_exec "$BOX_ID" "smoke: unit starts, supervisord runs" \
    "sudo systemctl start glenn-daemon.service && sleep 5 && sudo systemctl is-active glenn-daemon.service && pgrep -f supervisord >/dev/null && sudo systemctl stop glenn-daemon.service"

# Sanity: the agent's visual self-validation loop must work on the template —
# headless Chromium + SwiftShader software WebGL (boxes have no GPU). The probe
# page draws a red frame via WebGL (preserveDrawingBuffer so the readback is
# deterministic); snap-preview must report a live context and painted pixels.
WEBGL_PROBE_B64=$(base64 -w0 <<'HTML'
<!doctype html><canvas id="c" width="64" height="64"></canvas><script>
const gl = document.getElementById('c').getContext('webgl', { preserveDrawingBuffer: true });
if (gl) { gl.clearColor(1, 0, 0, 1); gl.clear(gl.COLOR_BUFFER_BIT); }
document.title = gl ? 'webgl-ok' : 'webgl-missing';
</script>
HTML
)
box_exec "$BOX_ID" "smoke: snap-preview renders WebGL (SwiftShader)" \
    "echo '$WEBGL_PROBE_B64' | base64 -d | sudo tee /tmp/webgl-probe.html >/dev/null && sudo -u agent env PLAYWRIGHT_BROWSERS_PATH=/opt/playwright-browsers snap-preview file:///tmp/webgl-probe.html --wait 500 --out /tmp/webgl-probe.png | sudo tee /tmp/webgl-probe.json && grep -q '\"contextAvailable\": true' /tmp/webgl-probe.json && grep -q '\"anyCanvasPainted\": true' /tmp/webgl-probe.json && test -s /tmp/webgl-probe.png"

if [[ "${KEEP_RUNNING:-0}" == "1" ]]; then
    echo "⚠️  KEEP_RUNNING=1 — leaving the box up for inspection. Stop it manually to snapshot:"
    echo "    curl -X POST $BOX_API_BASE_URL/boxes/$BOX_ID/stop -H 'Authorization: Bearer \$BOX_API_KEY'"
    exit 0
fi

echo "💾 Stopping the box (Box snapshots the disk — the snapshot IS the template) ..."
api POST "/boxes/$BOX_ID/stop" "{}" >/dev/null
wait_for_status "$BOX_ID" "== 'archived'" >/dev/null
echo "✅ template box archived: $BOX_ID (label: $LABEL)"

if [[ -n "${REGISTER_URL:-}" && -n "${CI_PUBLISH_KEY:-}" ]]; then
    echo "📮 Registering template with the platform ..."
    BUILT_AT=$(date -u +%Y-%m-%dT%H:%M:%SZ)
    curl -fsS -X POST "$REGISTER_URL/api/admin/runtime-templates" \
        -H "Authorization: Bearer $CI_PUBLISH_KEY" \
        -H "Content-Type: application/json" \
        -d "$(python3 -c 'import json,sys; print(json.dumps({"boxId": sys.argv[1], "label": sys.argv[2], "gitSha": sys.argv[3], "builtAt": sys.argv[4], "notes": None}))' "$BOX_ID" "$LABEL" "$GIT_SHA" "$BUILT_AT")" \
        >/dev/null
    echo "✅ registered + activated as the default fork source"
else
    echo "ℹ️  Not registered (set REGISTER_URL + CI_PUBLISH_KEY, or register in Super Admin → Runtime Templates):"
    echo "    boxId:  $BOX_ID"
    echo "    label:  $LABEL"
    echo "    gitSha: $GIT_SHA"
fi

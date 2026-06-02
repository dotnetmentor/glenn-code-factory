#!/usr/bin/env bash
set -euo pipefail

# Persistent Cloudflare quick tunnel for local dev.
# Started once, reused across npm run dev restarts; npm run dev exit does not stop it.
# Stop manually: npm run dev:tunnel:stop

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
STATE_DIR="$ROOT/.dev/cloudflare-tunnel"
PID_FILE="$STATE_DIR/pid"
URL_FILE="$STATE_DIR/url"
PORT_FILE="$STATE_DIR/api_port"
LOG_FILE="$STATE_DIR/cloudflared.log"
LOCK_FILE="$STATE_DIR/.ensure.lock"
API_PORT="${DEV_API_PORT:-5338}"

mkdir -p "$STATE_DIR"

ensure_cloudflared() {
  if command -v cloudflared >/dev/null 2>&1; then
    return 0
  fi
  if [[ "$(uname -s)" == "Darwin" ]] && command -v brew >/dev/null 2>&1; then
    echo "cloudflared not found — installing via Homebrew (one-time)..."
    brew install cloudflared
    return 0
  fi
  echo "cloudflared is required for npm run dev (Fly runtimes need a public API URL)."
  echo "Install: https://developers.cloudflare.com/cloudflare-one/connections/connect-networks/downloads/"
  exit 1
}

with_lock() {
  exec 9>"$LOCK_FILE"
  if ! flock -x 9 2>/dev/null; then
    # macOS: flock may be missing; fall back to mkdir lock
    local try=0
    while ! mkdir "$STATE_DIR/.ensure.lockdir" 2>/dev/null; do
      try=$((try + 1))
      if [[ $try -ge 40 ]]; then
        echo "Timed out waiting for dev tunnel lock." >&2
        exit 1
      fi
      sleep 0.1
    done
    "$@"
    rmdir "$STATE_DIR/.ensure.lockdir"
    return
  fi
  "$@"
}

pid_is_cloudflared() {
  local pid="$1"
  kill -0 "$pid" 2>/dev/null || return 1
  local cmdline
  cmdline="$(ps -p "$pid" -o command= 2>/dev/null || true)"
  [[ "$cmdline" == *cloudflared*tunnel* ]]
}

read_saved_url() {
  if [[ -f "$URL_FILE" ]]; then
    tr -d '[:space:]' <"$URL_FILE"
  fi
}

clear_state_files() {
  rm -f "$PID_FILE" "$URL_FILE" "$PORT_FILE"
}

tunnel_url_healthy() {
  local url="$1"
  [[ -n "$url" ]] || return 1

  # curl prints http_code=000 on DNS/connect failures AND exits non-zero.
  # Do NOT use `|| echo 000` here — that concatenates to "000000" and falsely
  # passes a `!= 000` check (dead quick-tunnel URLs look healthy).
  local code
  code="$(curl -sS -o /dev/null -w '%{http_code}' --max-time 8 "${url%/}/health" 2>/dev/null || true)"
  code="${code:-000}"
  [[ "$code" =~ ^2[0-9]{2}$ ]]
}

stop_saved_tunnel_process() {
  if [[ ! -f "$PID_FILE" ]]; then
    clear_state_files
    return 0
  fi

  local pid
  pid="$(tr -d '[:space:]' <"$PID_FILE")"
  if [[ -n "$pid" ]] && pid_is_cloudflared "$pid"; then
    kill "$pid" 2>/dev/null || true
    echo "Stopped cloudflared (pid $pid)." >&2
  fi

  clear_state_files
}

tunnel_process_ok() {
  [[ -f "$PID_FILE" ]] || return 1
  local pid url saved_port
  pid="$(tr -d '[:space:]' <"$PID_FILE")"
  [[ -n "$pid" ]] || return 1
  pid_is_cloudflared "$pid" || return 1

  url="$(read_saved_url)"
  [[ -n "$url" ]] || return 1

  saved_port=""
  if [[ -f "$PORT_FILE" ]]; then
    saved_port="$(tr -d '[:space:]' <"$PORT_FILE")"
  fi
  if [[ -n "$saved_port" && "$saved_port" != "$API_PORT" ]]; then
    return 1
  fi

  return 0
}

tunnel_reusable() {
  if ! tunnel_process_ok; then
    return 1
  fi

  local url saved_port
  url="$(read_saved_url)"
  saved_port=""
  if [[ -f "$PORT_FILE" ]]; then
    saved_port="$(tr -d '[:space:]' <"$PORT_FILE")"
  fi
  if [[ -n "$saved_port" && "$saved_port" != "$API_PORT" ]]; then
    echo "Saved tunnel targets port $saved_port but DEV_API_PORT=$API_PORT — starting a new tunnel." >&2
    stop_saved_tunnel_process
    return 1
  fi

  if ! tunnel_url_healthy "$url"; then
    echo "Saved tunnel URL is unreachable (DNS or /health failed) — restarting cloudflared." >&2
    stop_saved_tunnel_process
    return 1
  fi

  return 0
}

launch_detached_cloudflared() {
  # Must survive npm/concurrently Ctrl+C. Bash subshell + disown is not enough on
  # macOS — cloudflared was getting SIGINT ~3s after start. start_new_session
  # fully detaches from the dev terminal process group.
  LOG_FILE="$LOG_FILE" API_PORT="$API_PORT" python3 - <<'PY'
import os
import subprocess
import sys

log_path = os.environ["LOG_FILE"]
port = os.environ["API_PORT"]

with open(log_path, "a", encoding="utf-8") as log_file:
    proc = subprocess.Popen(
        [
            "cloudflared",
            "tunnel",
            "--url",
            f"http://127.0.0.1:{port}",
        ],
        stdin=subprocess.DEVNULL,
        stdout=log_file,
        stderr=subprocess.STDOUT,
        start_new_session=True,
    )

print(proc.pid)
PY
}

wait_for_tunnel_url() {
  local pid="$1"
  local url=""
  for _ in $(seq 1 120); do
    url="$(grep -oE 'https://[a-z0-9-]+\.trycloudflare\.com' "$LOG_FILE" | tail -1 || true)"
    if [[ -n "$url" ]]; then
      echo "$url"
      return 0
    fi
    if ! kill -0 "$pid" 2>/dev/null; then
      echo "cloudflared exited before publishing a URL. Log:" >&2
      tail -30 "$LOG_FILE" >&2
      return 1
    fi
    sleep 0.25
  done
  echo "Failed to obtain a Cloudflare quick-tunnel URL. cloudflared output:" >&2
  tail -30 "$LOG_FILE" >&2
  return 1
}

start_tunnel() {
  ensure_cloudflared

  : >"$LOG_FILE"

  local pid url
  pid="$(launch_detached_cloudflared)"
  url="$(wait_for_tunnel_url "$pid")" || {
    kill "$pid" 2>/dev/null || true
    exit 1
  }

  printf '%s\n' "$pid" >"$PID_FILE"
  printf '%s\n' "$url" >"$URL_FILE"
  printf '%s\n' "$API_PORT" >"$PORT_FILE"
  echo "STARTED"
  echo "$url"
}

stop_tunnel() {
  if [[ ! -f "$PID_FILE" ]]; then
    echo "No persistent dev tunnel running (no pid file)."
    clear_state_files
    return 0
  fi

  stop_saved_tunnel_process
  echo "Dev tunnel stopped."
}

status_tunnel() {
  if ! tunnel_process_ok; then
    echo "not running"
    exit 1
  fi

  local pid url health
  pid="$(tr -d '[:space:]' <"$PID_FILE")"
  url="$(read_saved_url)"
  if tunnel_url_healthy "$url"; then
    health=ok
  else
    health=unreachable
  fi
  echo "running pid=$pid url=$url api_port=${API_PORT} health=$health"
  if [[ "$health" != "ok" ]]; then
    echo "Tunnel process is alive but the public URL is not serving /health — run: npm run dev:tunnel:stop && npm run dev" >&2
    exit 1
  fi
}

ensure_tunnel() {
  if tunnel_reusable; then
    echo "REUSED"
    read_saved_url
    return 0
  fi

  # Dead/stale state only — do not kill unrelated cloudflared processes.
  if [[ -f "$PID_FILE" ]]; then
    clear_state_files
  fi

  start_tunnel
}

case "${1:-ensure}" in
  ensure) with_lock ensure_tunnel ;;
  start) with_lock start_tunnel ;;
  stop) with_lock stop_tunnel ;;
  status) status_tunnel ;;
  *)
    echo "Usage: $0 {ensure|start|stop|status}" >&2
    exit 1
    ;;
esac

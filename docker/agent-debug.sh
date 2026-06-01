#!/usr/bin/env bash
# =====================================================================================
# Glenn Runtime — agent-debug
# =====================================================================================
# One-shot triage snapshot for a Fly Machine running the agent daemon. Designed so an
# agent (or human) SSH'd into the machine can paste a single command and get every
# fact that matters for diagnosing "runtime stuck Bootstrapping" or "daemon won't
# boot" without having to remember 12 separate paths/commands.
#
# Usage:
#   agent-debug              # full snapshot
#   agent-debug logs         # just tail the daemon logs
#   agent-debug logs -f      # follow the daemon logs
#   agent-debug sdk         # just check the Cursor SDK bundle in /opt/agent
#   agent-debug env          # just print the daemon-relevant env vars
#   agent-debug sha          # just compare cached sha vs env sha (the trap)
#
# Exit codes are best-effort informational — always 0 unless invoked wrong.
# =====================================================================================
set -uo pipefail

mode="${1:-all}"

c_dim()  { printf '\033[2m%s\033[0m' "$1"; }
c_bold() { printf '\033[1m%s\033[0m' "$1"; }
c_red()  { printf '\033[31m%s\033[0m' "$1"; }
c_grn()  { printf '\033[32m%s\033[0m' "$1"; }
c_ylw()  { printf '\033[33m%s\033[0m' "$1"; }
hr() { printf '%s\n' "----------------------------------------------------------------"; }
hdr() { echo; c_bold "== $1 =="; echo; }

# ---------------------------- env ----------------------------------------------------
show_env() {
  hdr "Daemon env (from supervisord [program:agent])"
  for k in RUNTIME_ID DAEMON_VERSION DAEMON_BUNDLE_URL DAEMON_BUNDLE_SHA256 \
           MAIN_API_URL GLENN_RUNTIME_TOKEN NODE_ENV; do
    # Walk supervisord's view of the env so we see what the daemon actually got,
    # not whatever the SSH session inherits (which is usually empty).
    v="$(supervisorctl -c /etc/supervisor/supervisord.conf pid agent 2>/dev/null \
         | xargs -I{} cat /proc/{}/environ 2>/dev/null \
         | tr '\0' '\n' | grep -E "^${k}=" | head -1 | cut -d= -f2-)"
    if [[ -z "$v" ]]; then
      v="${!k:-<unset>}"
    fi
    case "$k" in
      GLENN_RUNTIME_TOKEN) [[ "$v" != "<unset>" ]] && v="${v:0:8}…(${#v} chars)" ;;
      DAEMON_BUNDLE_SHA256)  [[ "$v" != "<unset>" ]] && v="${v:0:16}…" ;;
    esac
    printf '  %-22s %s\n' "$k" "$v"
  done
}

# ---------------------------- sha trap -----------------------------------------------
# bootstrap-daemon.sh caches the extracted bundle by writing the env sha to
# /opt/agent/.bundle.sha256. If those drift, the bootstrap will re-download +
# overwrite anything you hot-swapped. This subcommand catches that fast.
show_sha() {
  hdr "Bundle SHA cache check (the hot-swap trap)"
  local cached env_sha
  cached="$(cat /opt/agent/.bundle.sha256 2>/dev/null || echo '<missing>')"
  env_sha="$(supervisorctl -c /etc/supervisor/supervisord.conf pid agent 2>/dev/null \
           | xargs -I{} cat /proc/{}/environ 2>/dev/null \
           | tr '\0' '\n' | grep -E '^DAEMON_BUNDLE_SHA256=' | head -1 | cut -d= -f2)"
  env_sha="${env_sha:-${DAEMON_BUNDLE_SHA256:-<unset>}}"
  printf '  /opt/agent/.bundle.sha256  %s\n' "$cached"
  printf '  $DAEMON_BUNDLE_SHA256      %s\n' "$env_sha"
  if [[ "$cached" == "$env_sha" && "$cached" != '<missing>' ]]; then
    printf '  '; c_grn 'MATCH'; echo ' — bootstrap will use cached /opt/agent/daemon.js'
  else
    printf '  '; c_ylw 'MISMATCH'; echo ' — next bootstrap will RE-DOWNLOAD and overwrite /opt/agent'
    echo
    echo '  If you hot-swapped daemon.js and want it to STICK across supervisord'
    echo '  restarts, write the env sha into the file:'
    printf '    \033[36mprintf %%s "$DAEMON_BUNDLE_SHA256" > /opt/agent/.bundle.sha256\033[0m\n'
  fi
}

# ---------------------------- bundle on disk -----------------------------------------
show_bundle() {
  hdr "Extracted bundle (/opt/agent)"
  if [[ -d /opt/agent ]]; then
    ls -la /opt/agent 2>/dev/null | head -20
    if [[ -f /opt/agent/daemon.js ]]; then
      printf '  daemon.js: %s bytes\n' "$(stat -c%s /opt/agent/daemon.js)"
      # Quick parse check — catches the createRequire-class trap (banner +
      # source-level import collide → daemon dies before logging anything).
      if node --check /opt/agent/daemon.js 2>/tmp/daemon-parse.err; then
        printf '  '; c_grn 'node --check'; echo ': PARSES OK'
      else
        printf '  '; c_red 'node --check'; echo ': PARSE ERROR ↓'
        sed 's/^/    /' /tmp/daemon-parse.err
      fi
    else
      c_red '  daemon.js MISSING'; echo
    fi
  else
    c_red '  /opt/agent does not exist'; echo
  fi
}

# ---------------------------- Cursor SDK bundle --------------------------------------
show_cursor_sdk() {
  hdr "Cursor SDK bundle (/opt/agent/node_modules)"
  local sdk_dir="/opt/agent/node_modules/@cursor/sdk"
  local sqlite="/opt/agent/node_modules/sqlite3/build/Release/node_sqlite3.node"
  if [[ -d "$sdk_dir" ]]; then
    c_grn "  @cursor/sdk present"; echo "  path: $sdk_dir"
    if [[ -f "$sdk_dir/package.json" ]]; then
      printf '  version: %s\n' "$(node -p "require('$sdk_dir/package.json').version" 2>/dev/null || echo '?')"
    fi
  else
    c_red "  @cursor/sdk MISSING at $sdk_dir"; echo
  fi
  if [[ -f "$sqlite" ]]; then
    printf '  sqlite3 binding: %s (%s bytes)\n' "$sqlite" "$(stat -c%s "$sqlite")"
  else
    c_red "  sqlite3 native binding MISSING (required by @cursor/sdk)"; echo
    echo '  Republish the daemon bundle with ./scripts/publish-daemon.sh'
  fi
}

# ---------------------------- memory / disk ------------------------------------------
show_resources() {
  hdr "Memory + disk"
  free -m | sed 's/^/  /'
  echo
  df -h / /data /opt 2>/dev/null | sed 's/^/  /'
}

# ---------------------------- supervisord --------------------------------------------
show_supervisor() {
  hdr "supervisorctl status"
  supervisorctl -c /etc/supervisor/supervisord.conf status 2>&1 | sed 's/^/  /'
  echo
  echo '  Recovery from FATAL ("too many start retries"):'
  echo '    supervisorctl stop agent && supervisorctl reread && \'
  echo '    supervisorctl update    && supervisorctl start agent'
}

# ---------------------------- logs ---------------------------------------------------
LOG_OUT=/var/log/supervisor/agent.out.log
LOG_ERR=/var/log/supervisor/agent.err.log
LOG_SUP=/var/log/supervisor/supervisord.log

show_logs() {
  local follow=""
  [[ "${1:-}" == "-f" || "${1:-}" == "--follow" ]] && follow="-f"

  if [[ -n "$follow" ]]; then
    echo "Following $LOG_OUT and $LOG_ERR (Ctrl-C to stop)…"
    exec tail -f "$LOG_OUT" "$LOG_ERR"
  fi

  hdr "daemon stdout — last 50 lines  ($LOG_OUT)"
  if [[ -f "$LOG_OUT" ]]; then tail -n 50 "$LOG_OUT" | sed 's/^/  /'; else c_dim '  (no log yet)'; echo; fi

  hdr "daemon stderr — last 50 lines  ($LOG_ERR)"
  if [[ -f "$LOG_ERR" ]]; then tail -n 50 "$LOG_ERR" | sed 's/^/  /'; else c_dim '  (no log yet)'; echo; fi

  hdr "supervisord itself — last 20 lines  ($LOG_SUP)"
  if [[ -f "$LOG_SUP" ]]; then tail -n 20 "$LOG_SUP" | sed 's/^/  /'; else c_dim '  (no log yet)'; echo; fi
}

# ---------------------------- heartbeat probe ----------------------------------------
show_heartbeat() {
  hdr "Heartbeat reachability probe (no auth, just connectivity)"
  local api
  api="$(supervisorctl -c /etc/supervisor/supervisord.conf pid agent 2>/dev/null \
       | xargs -I{} cat /proc/{}/environ 2>/dev/null \
       | tr '\0' '\n' | grep -E '^MAIN_API_URL=' | head -1 | cut -d= -f2)"
  api="${api:-${MAIN_API_URL:-}}"
  if [[ -z "$api" ]]; then c_red '  MAIN_API_URL unset'; echo; return; fi
  printf '  MAIN_API_URL = %s\n' "$api"
  printf '  GET %s/health → ' "$api"
  curl -sS -o /tmp/health.out -w 'HTTP %{http_code} (%{time_total}s)\n' --max-time 6 "$api/health" \
    || echo 'curl failed'
  [[ -s /tmp/health.out ]] && head -c 200 /tmp/health.out | sed 's/^/    /' && echo
}

# ---------------------------- main ---------------------------------------------------
case "$mode" in
  env)        show_env ;;
  sha)        show_sha ;;
  bundle)     show_bundle ;;
  sdk)        show_cursor_sdk ;;
  claude)     echo "Note: 'claude' subcommand renamed to 'sdk' (Cursor SDK pivot)." >&2; show_cursor_sdk ;;
  resources|res|mem) show_resources ;;
  supervisor|sv) show_supervisor ;;
  logs)       show_logs "${2:-}" ;;
  heartbeat|hb) show_heartbeat ;;
  all|"")
    show_env
    show_sha
    show_resources
    show_supervisor
    show_bundle
    show_cursor_sdk
    show_heartbeat
    show_logs
    hr
    c_dim "Subcommands: env | sha | bundle | sdk | resources | supervisor | logs [-f] | heartbeat"
    echo
    ;;
  *)
    echo "Unknown subcommand: $mode"
    echo "Usage: agent-debug [env|sha|bundle|sdk|resources|supervisor|logs [-f]|heartbeat|all]"
    exit 2
    ;;
esac

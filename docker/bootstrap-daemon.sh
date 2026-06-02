#!/usr/bin/env bash
set -euo pipefail

# bootstrap-daemon.sh — resolve, download, and exec the daemon bundle.

: "${MAIN_API_URL:?MAIN_API_URL env var is required}"

CHANNEL="${RUNTIME_CHANNEL:-stable}"

DEST="${DAEMON_DEST:-/opt/agent}"
TARBALL="/tmp/daemon-bundle.tar.gz"
SHA_FILE="${DEST}/.bundle.sha256"

log() { printf '[bootstrap-daemon] %s\n' "$*" >&2; }
fail() { log "FATAL: $*"; exit 1; }

resolve_json() {
  local endpoint="$1"
  curl -fsS --retry 5 --retry-delay 2 --max-time 15 \
       "$MAIN_API_URL/api/$endpoint/resolve?channel=$CHANNEL"
}

json_field() {
  local field="$1"
  python3 -c "import sys, json; print(json.load(sys.stdin)['$field'])"
}

log "resolving daemon version (channel=$CHANNEL) at $MAIN_API_URL"
DAEMON_RESOLVED="$(resolve_json daemon-versions)" \
  || fail "could not resolve daemon-versions (channel=$CHANNEL) from $MAIN_API_URL"
DAEMON_VERSION="$(printf '%s' "$DAEMON_RESOLVED" | json_field version)"
DAEMON_BUNDLE_URL="$(printf '%s' "$DAEMON_RESOLVED" | json_field downloadUrl)"
DAEMON_BUNDLE_SHA256="$(printf '%s' "$DAEMON_RESOLVED" | json_field sha256)"
log "  daemon: version=$DAEMON_VERSION sha=$DAEMON_BUNDLE_SHA256"

if [[ -f "$SHA_FILE" ]] && [[ "$(cat "$SHA_FILE")" == "$DAEMON_BUNDLE_SHA256" ]] && [[ -f "$DEST/daemon.js" ]]; then
  log "daemon cache hit (sha=$DAEMON_BUNDLE_SHA256), skipping download"
else
  bundle_url="$DAEMON_BUNDLE_URL"
  if [[ "$bundle_url" != http://* && "$bundle_url" != https://* ]]; then
    bundle_url="${MAIN_API_URL%/}${bundle_url}"
  fi
  log "downloading daemon bundle from $bundle_url"
  curl -fsSL --retry 3 --retry-delay 2 --max-time 60 -o "$TARBALL" "$bundle_url"

  ACTUAL=$(sha256sum "$TARBALL" | awk '{print $1}')
  if [[ "$ACTUAL" != "$DAEMON_BUNDLE_SHA256" ]]; then
    log "FATAL: daemon sha256 mismatch (expected $DAEMON_BUNDLE_SHA256, got $ACTUAL)"
    exit 2
  fi
  log "daemon sha256 verified"

  mkdir -p "$DEST"
  find "$DEST" -mindepth 1 -delete
  tar -xzf "$TARBALL" -C "$DEST"
  rm "$TARBALL"
  printf '%s' "$DAEMON_BUNDLE_SHA256" > "$SHA_FILE"
  log "daemon extracted to $DEST"
fi

if [[ ! -f "$DEST/daemon.js" ]]; then
  log "FATAL: $DEST/daemon.js missing after extract"
  exit 3
fi

log "exec node $DEST/daemon.js (daemon_version=$DAEMON_VERSION)"
exec node "$DEST/daemon.js"

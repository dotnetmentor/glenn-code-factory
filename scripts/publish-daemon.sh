#!/usr/bin/env bash
set -euo pipefail

API="${API:-http://localhost:5338}"
CHANNEL="${CHANNEL:-stable}"
JWT_FILE="${JWT_FILE:-/tmp/jwt.txt}"

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
DAEMON_DIR="$ROOT/packages/daemon"
STAGE="$ROOT/.daemon-publish-stage"
TARBALL="$ROOT/.daemon-bundle.tar.gz"

log() { printf '\033[36m[publish-daemon]\033[0m %s\n' "$*"; }
fail() { printf '\033[31m[publish-daemon FAIL]\033[0m %s\n' "$*" >&2; exit 1; }

[[ -f "$JWT_FILE" ]] || fail "JWT file $JWT_FILE not found. Mint one with /tmp/mint-jwt.mjs"
JWT="$(cat "$JWT_FILE")"

# 1. Build daemon
log "building daemon (esbuild)..."
( cd "$DAEMON_DIR" && npm run build ) || fail "daemon build failed"
[[ -f "$DAEMON_DIR/dist/main.js" ]] || fail "expected $DAEMON_DIR/dist/main.js after build"

# 2. Stage layout
log "staging bundle..."
rm -rf "$STAGE"
mkdir -p "$STAGE"
cp "$DAEMON_DIR/dist/main.js" "$STAGE/daemon.js"

# 3. Install runtime deps that esbuild can't follow
#    - SignalR peer deps (ws/eventsource/etc — required by @microsoft/signalr at runtime)
#    - @cursor/sdk (marked external in esbuild — must be installed at publish time)
log "installing runtime peer deps..."
mkdir -p "$STAGE/node_modules-temp"
cd "$STAGE/node_modules-temp"
CURSOR_SDK_VERSION="$(node -p "require('$DAEMON_DIR/package.json').dependencies['@cursor/sdk']")"
log "  cursor SDK version: $CURSOR_SDK_VERSION"
cat > package.json <<JSON
{
  "name": "daemon-bundle-peers",
  "version": "0.0.0",
  "private": true,
  "dependencies": {
    "@cursor/sdk": "$CURSOR_SDK_VERSION",
    "ws": "^8.18.0",
    "eventsource": "^2.0.2",
    "tough-cookie": "^4.1.4",
    "node-fetch": "^2.7.0",
    "fetch-cookie": "^2.2.0",
    "abort-controller": "^3.0.0"
  }
}
JSON
# Force linux-x64 platform selection so optional deps resolve to the target arch
# even when the publish host is arm64 (e.g. agent container on Apple Silicon).
npm install --omit=dev --cpu=x64 --os=linux --ignore-scripts --silent --no-audit --no-fund --no-package-lock 2>&1 | tail -5

# --- Native bindings: sqlite3 -------------------------------------------------
# `--ignore-scripts` above skipped sqlite3's postinstall ("prebuild-install -r
# napi || node-gyp rebuild"), so node_modules/sqlite3/build/Release/node_sqlite3.node
# is missing — and the daemon's @cursor/sdk crashes at runtime with
# "Could not locate the bindings file".
if [[ -d node_modules/sqlite3 ]]; then
  log "fetching sqlite3 native binding (prebuild-install, linux-x64, napi)..."
  ( cd node_modules/sqlite3 && ../.bin/prebuild-install --runtime=napi --platform=linux --arch=x64 ) \
    || fail "prebuild-install failed for sqlite3 (linux-x64 napi). The @cursor/sdk depends on this binding."
  SQLITE_BINDING="node_modules/sqlite3/build/Release/node_sqlite3.node"
  [[ -f "$SQLITE_BINDING" ]] || fail "sqlite3 native binding missing at $SQLITE_BINDING after prebuild-install"
  log "  sqlite3 native binding present at $SQLITE_BINDING ($(stat -c%s "$SQLITE_BINDING") bytes)"
else
  log "  sqlite3 not in node_modules — skipping native binding step"
fi

# Sanity check: the cursor SDK must be present — external-at-build /
# installed-at-publish. The daemon does `import('@cursor/sdk')` at runtime.
CURSOR_SDK_JS_DIR="node_modules/@cursor/sdk"
[[ -d "$CURSOR_SDK_JS_DIR" ]] || fail "Cursor SDK JS missing at $CURSOR_SDK_JS_DIR (npm install failed?)"
log "  Cursor SDK JS present at $CURSOR_SDK_JS_DIR"

mv node_modules "$STAGE/node_modules"
cd "$STAGE"
rm -rf "$STAGE/node_modules-temp"

# 4. Tarball
log "creating tarball..."
rm -f "$TARBALL"
tar -czf "$TARBALL" -C "$STAGE" daemon.js node_modules
SHA=$(sha256sum "$TARBALL" | awk '{print $1}')
SIZE=$(stat -c%s "$TARBALL")
log "bundle: $TARBALL"
log "  size: $SIZE bytes"
log "  sha:  $SHA"

# 5. Publish via API
log "publishing to $API/api/daemon-versions (channel=$CHANNEL)..."
NOTES="auto-published $(date -u +%Y-%m-%dT%H:%M:%SZ)"

RESP=$(curl -fsS -X POST "$API/api/daemon-versions" \
  -H "Authorization: Bearer $JWT" \
  -F "file=@$TARBALL;type=application/gzip" \
  -F "channel=$CHANNEL" \
  -F "notes=$NOTES" \
  -F "preComputedSha256=$SHA") || fail "publish HTTP request failed"

log "published"
echo "$RESP" | python3 -m json.tool || echo "$RESP"

# 6. Verify resolve endpoint sees it
log "verifying resolve..."
curl -fsS "$API/api/daemon-versions/resolve?channel=$CHANNEL" | python3 -m json.tool || true

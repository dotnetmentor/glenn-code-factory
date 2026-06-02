#!/usr/bin/env bash
# Register a runtime base image in the control plane (POST /api/admin/runtime-images).
#
# Required env:
#   CONTROL_PLANE_API (or API)
#   CONTROL_PLANE_PUBLISH_API_KEY (or platform_auth_jwt via .env for local dev)
#   RUNTIME_IMAGE_TAG, RUNTIME_IMAGE_DIGEST, RUNTIME_IMAGE_SIZE_MB
#   REGISTRY (full path, e.g. registry.fly.io/glenn-runtime-base)
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
API="${CONTROL_PLANE_API:-${API:-}}"
TAG="${RUNTIME_IMAGE_TAG:?RUNTIME_IMAGE_TAG required}"
DIGEST="${RUNTIME_IMAGE_DIGEST:?RUNTIME_IMAGE_DIGEST required}"
SIZE_MB="${RUNTIME_IMAGE_SIZE_MB:-0}"
REGISTRY="${REGISTRY:-registry.fly.io/glenn-runtime-base}"
FULL_SHA="$(git -C "$REPO_ROOT" rev-parse HEAD)"
BUILT_AT="$(date -u +%Y-%m-%dT%H:%M:%SZ)"

# shellcheck source=../lib/ci-publish-auth.sh
source "$REPO_ROOT/scripts/lib/ci-publish-auth.sh"

log() { printf '\033[36m[register-runtime-image]\033[0m %s\n' "$*"; }
fail() { printf '\033[31m[register-runtime-image FAIL]\033[0m %s\n' "$*" >&2; exit 1; }

AUTH="$(ci_publish_auth_header)" || fail "set CONTROL_PLANE_PUBLISH_API_KEY or configure local .env for JWT"

log "registering tag=$TAG digest=$DIGEST"
REG_BODY="$(python3 -c "
import json
print(json.dumps({
  'tag': '$TAG',
  'digest': '$DIGEST',
  'registry': '$REGISTRY',
  'gitSha': '$FULL_SHA',
  'builtAt': '$BUILT_AT',
  'sizeMb': int('$SIZE_MB'),
  'notes': 'published via GitHub Actions',
}))")"

REG_RESP="$(curl -fsS -X POST "${API%/}/api/admin/runtime-images" \
  -H "$AUTH" \
  -H "Content-Type: application/json" \
  -d "$REG_BODY")" || fail "register HTTP call failed"
echo "$REG_RESP" | python3 -m json.tool 2>/dev/null || echo "$REG_RESP"
log "done (registered as Active; prior Active rows demoted to Deprecated)"

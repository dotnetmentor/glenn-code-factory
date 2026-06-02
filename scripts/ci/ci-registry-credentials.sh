#!/usr/bin/env bash
# Fetch Fly registry credentials once (GET /api/ci/registry-credentials).
# Exports: FLY_API_TOKEN, REGISTRY_HOST, REGISTRY_USER, REGISTRY_PASS
# Idempotent when sourced multiple times in the same shell.
set -euo pipefail

if [[ -n "${CI_REGISTRY_CREDENTIALS_LOADED:-}" ]]; then
  return 0
fi

API="${CONTROL_PLANE_API:-${API:-}}"
KEY="${CONTROL_PLANE_PUBLISH_API_KEY:-}"

if [[ -z "$API" || -z "$KEY" ]]; then
  echo "CONTROL_PLANE_API and CONTROL_PLANE_PUBLISH_API_KEY are required" >&2
  exit 1
fi

JSON="$(curl -fsS "${API%/}/api/ci/registry-credentials" \
  -H "X-Ci-Publish-Key: ${KEY}")"

REGISTRY_HOST="$(printf '%s' "$JSON" | python3 -c "import sys,json; print(json.load(sys.stdin)['registryHost'])")"
REGISTRY_USER="$(printf '%s' "$JSON" | python3 -c "import sys,json; print(json.load(sys.stdin)['username'])")"
REGISTRY_PASS="$(printf '%s' "$JSON" | python3 -c "import sys,json; print(json.load(sys.stdin)['password'])")"

[[ -n "$REGISTRY_PASS" ]] || { echo "registry-credentials returned empty password" >&2; exit 1; }

export FLY_API_TOKEN="$REGISTRY_PASS"
export REGISTRY_HOST REGISTRY_USER REGISTRY_PASS
export CI_REGISTRY_CREDENTIALS_LOADED=1

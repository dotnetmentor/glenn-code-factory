#!/usr/bin/env bash
# Docker login to registry.fly.io using GET /api/ci/registry-credentials.
set -euo pipefail

API="${CONTROL_PLANE_API:-${API:-}}"
KEY="${CONTROL_PLANE_PUBLISH_API_KEY:-}"

if [[ -z "$API" || -z "$KEY" ]]; then
  echo "CONTROL_PLANE_API and CONTROL_PLANE_PUBLISH_API_KEY are required" >&2
  exit 1
fi

JSON="$(curl -fsS "${API%/}/api/ci/registry-credentials" \
  -H "X-Ci-Publish-Key: ${KEY}")"

HOST="$(printf '%s' "$JSON" | python3 -c "import sys,json; print(json.load(sys.stdin)['registryHost'])")"
USER="$(printf '%s' "$JSON" | python3 -c "import sys,json; print(json.load(sys.stdin)['username'])")"
PASS="$(printf '%s' "$JSON" | python3 -c "import sys,json; print(json.load(sys.stdin)['password'])")"

printf '%s' "$PASS" | docker login "$HOST" -u "$USER" --password-stdin

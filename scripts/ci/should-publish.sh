#!/usr/bin/env bash
# Exit 0 when a publish pipeline should run; exit 1 to skip.
#
# Usage:
#   scripts/ci/should-publish.sh daemon
#   scripts/ci/should-publish.sh runtime
#
# Env:
#   FORCE=1
#   BEFORE_SHA                 github.event.before
#   CONTROL_PLANE_API
#   CONTROL_PLANE_PUBLISH_API_KEY
set -euo pipefail

TARGET="${1:-}"
if [[ "$TARGET" != "daemon" && "$TARGET" != "runtime" ]]; then
  echo "usage: should-publish.sh <daemon|runtime>" >&2
  exit 2
fi

REPO_ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
cd "$REPO_ROOT"

# shellcheck source=publish-paths.sh
source "$REPO_ROOT/scripts/ci/publish-paths.sh"

if [[ "${FORCE:-}" == "1" || "${FORCE:-}" == "true" ]]; then
  echo "FORCE set — will publish $TARGET"
  exit 0
fi

HEAD_SHA="$(git rev-parse HEAD)"

if [[ -z "${BEFORE_SHA:-}" || "$BEFORE_SHA" == "0000000000000000000000000000000000000000" ]]; then
  CHANGED="$(git diff-tree --no-commit-id --name-only -r HEAD 2>/dev/null || git show --name-only --pretty=format: HEAD)"
else
  CHANGED="$(git diff --name-only "$BEFORE_SHA" "$HEAD_SHA" 2>/dev/null || true)"
fi

if [[ -z "$CHANGED" ]]; then
  echo "No changed files in range — skip $TARGET publish"
  exit 1
fi

relevant=false
while IFS= read -r file; do
  [[ -z "$file" ]] && continue
  if [[ "$TARGET" == "daemon" ]]; then
    matches_daemon_publish_path "$file" && relevant=true && break
  else
    matches_runtime_publish_path "$file" && relevant=true && break
  fi
done <<< "$CHANGED"

if ! $relevant; then
  echo "No $TARGET-relevant paths changed — skip publish"
  exit 1
fi

echo "$TARGET-relevant paths changed in $HEAD_SHA"

API="${CONTROL_PLANE_API:-${API:-}}"
KEY="${CONTROL_PLANE_PUBLISH_API_KEY:-}"
if [[ -z "$API" || -z "$KEY" ]]; then
  echo "CONTROL_PLANE_API or CONTROL_PLANE_PUBLISH_API_KEY unset — aborting (fail-closed)" >&2
  exit 1
fi

STATUS_JSON="$(curl -fsS "${API%/}/api/ci/publish-status?gitSha=${HEAD_SHA}" \
  -H "X-Ci-Publish-Key: ${KEY}")" || {
  echo "publish-status check failed — skip publish (fail-closed)" >&2
  exit 1
}

if [[ "$TARGET" == "daemon" ]]; then
  ALREADY="$(printf '%s' "$STATUS_JSON" | python3 -c "import sys,json; print('true' if json.load(sys.stdin).get('daemonPublishedForRequestedSha') else 'false')")"
else
  ALREADY="$(printf '%s' "$STATUS_JSON" | python3 -c "import sys,json; print('true' if json.load(sys.stdin).get('runtimePublishedForRequestedSha') else 'false')")"
fi

if [[ "$ALREADY" == "true" ]]; then
  echo "Already published $TARGET for git=${HEAD_SHA} — skip"
  exit 1
fi

echo "Not yet published for git=${HEAD_SHA} — will publish"
exit 0

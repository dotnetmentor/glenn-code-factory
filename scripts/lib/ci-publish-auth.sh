#!/usr/bin/env bash
# Auth helpers for CI publish calls. Uses X-Ci-Publish-Key (not Bearer) so JWT stays separate.
#
# Prefers CONTROL_PLANE_PUBLISH_API_KEY when set. Falls back to platform_auth_jwt
# for local dev when .env + Postgres are available.
set -euo pipefail

ci_publish_auth_header() {
  if [[ -n "${CONTROL_PLANE_PUBLISH_API_KEY:-}" ]]; then
    printf '%s: %s' "X-Ci-Publish-Key" "$CONTROL_PLANE_PUBLISH_API_KEY"
    return 0
  fi

  local root
  root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
  # shellcheck source=platform-auth.sh
  source "$root/scripts/lib/platform-auth.sh"
  local jwt
  jwt="$(platform_auth_jwt)" || return 1
  printf 'Authorization: Bearer %s' "$jwt"
}

#!/usr/bin/env bash
# Shared helpers for publish scripts — DB-backed auth only (no manual FLY_API_TOKEN / jwt.txt).
set -euo pipefail

PLATFORM_AUTH_LIB="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/platform-auth.mjs"

platform_auth_fly_token() {
    node "$PLATFORM_AUTH_LIB" fly-token
}

platform_auth_jwt() {
    node "$PLATFORM_AUTH_LIB" jwt
}

platform_auth_read_setting() {
    local key="$1"
    node "$PLATFORM_AUTH_LIB" decrypt "$key"
}

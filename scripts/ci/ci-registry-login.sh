#!/usr/bin/env bash
# Docker login to registry.fly.io using GET /api/ci/registry-credentials.
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
# shellcheck source=ci-registry-credentials.sh
source "$REPO_ROOT/scripts/ci/ci-registry-credentials.sh"

printf '%s' "$REGISTRY_PASS" | docker login "$REGISTRY_HOST" -u "$REGISTRY_USER" --password-stdin

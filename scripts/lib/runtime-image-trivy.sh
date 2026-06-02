#!/usr/bin/env bash
# OS-package Trivy gate for runtime base images.
# Usage: runtime_image_trivy_scan <image_ref> <repo_root> [required]
#   required: "true" to fail when trivy is missing (CI); default skips with a warning.
runtime_image_trivy_scan() {
    local image_ref="$1"
    local repo_root="$2"
    local required="${3:-false}"

    if ! command -v trivy >/dev/null 2>&1; then
        if [[ "$required" == "true" ]]; then
            echo "❌ trivy not installed (required for this publish)" >&2
            return 1
        fi
        echo "⚠️  trivy not installed — skipping vulnerability scan."
        echo "   Install with: brew install trivy   (or aquasecurity/trivy on Linux)"
        return 0
    fi

    if [[ -n "${CONTROL_PLANE_PUBLISH_API_KEY:-}" && -n "${CONTROL_PLANE_API:-${API:-}}" ]]; then
        # shellcheck source=../ci/ci-registry-login.sh
        bash "${repo_root}/scripts/ci/ci-registry-login.sh"
    fi

    echo "🛡️  Trivy scan ($image_ref) — OS packages only"
    trivy image \
        --severity CRITICAL \
        --ignore-unfixed \
        --pkg-types os \
        --exit-code 1 \
        "$image_ref"
}

#!/usr/bin/env bash
# =====================================================================================
# publish-runtime-image.sh
# =====================================================================================
# Builds, smoke-tests, and pushes the glenn-runtime-base image. Single source of
# truth for both local dev and CI — the GitHub workflow just calls this.
#
# Default registry is registry.fly.io because Fly Machines auto-authenticate to it
# (no pull secrets, no external provider). Override REGISTRY to point elsewhere.
#
# Push only happens here. On main, .github/workflows/runtime-base-image.yml also
# calls scripts/ci/register-runtime-image.sh, which POSTs to the control plane;
# that API registers the image as Active and demotes the previous Active row.
# Local-only runs (--no-push) do not touch the catalog — use the admin UI to
# register/activate manually if you are not going through CI.
#
# Trivy (when pushing): CRITICAL OS packages only (--pkg-types os). Vendor Go binaries
# (e.g. cloudflared) are not gated here — patch via CLOUDFLARED_VERSION / upstream releases.
# CI is the security gate; publish-runtime-image-remote.sh does not run Trivy.
#
# Usage:
#   scripts/publish-runtime-image.sh                       # full pipeline
#   scripts/publish-runtime-image.sh --no-push             # build + smoke-test only
#   scripts/publish-runtime-image.sh --skip-trivy          # skip vulnerability scan
#   scripts/publish-runtime-image.sh --skip-smoketest      # skip in-container probe
#   scripts/publish-runtime-image.sh --enforce-size-budget # gate on .image-size.last
#
# Required env (when pushing to registry.fly.io):
#   SystemSettings.Fly:ApiToken in Postgres + .env SystemSettings__EncryptionKey
#   (read automatically — no manual FLY_API_TOKEN)
#
# Optional env:
#   REGISTRY                Default: registry.fly.io
#   IMAGE_NAME              Default: glenn-runtime-base
#   REGISTRY_USERNAME       For non-Fly registries only.
#   REGISTRY_TOKEN          Password/token for non-Fly registries.
#   PLATFORM                Default: linux/amd64
#   GITHUB_OUTPUT           If set, the script appends tag/digest/size_mb to it
#                           (so a CI step can `${{ steps.id.outputs.tag }}` them).
# =====================================================================================

set -euo pipefail

# ---------- Defaults ----------------------------------------------------------------
REGISTRY="${REGISTRY:-registry.fly.io}"
IMAGE_NAME="${IMAGE_NAME:-glenn-runtime-base}"
PLATFORM="${PLATFORM:-linux/amd64}"
SIZE_BUDGET_FILE=".image-size.last"
SIZE_GROWTH_THRESHOLD_PCT="${SIZE_GROWTH_THRESHOLD_PCT:-10}"
REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
# shellcheck source=lib/platform-auth.sh
source "$REPO_ROOT/scripts/lib/platform-auth.sh"

# ---------- Flags -------------------------------------------------------------------
DO_PUSH=true
DO_TRIVY=true
DO_SMOKETEST=true
ENFORCE_SIZE_BUDGET=false

while [[ $# -gt 0 ]]; do
    case "$1" in
        --no-push)               DO_PUSH=false ;;
        --skip-trivy)            DO_TRIVY=false ;;
        --skip-smoketest)        DO_SMOKETEST=false ;;
        --enforce-size-budget)   ENFORCE_SIZE_BUDGET=true ;;
        -h|--help)
            cat <<'HELP'
publish-runtime-image.sh — build, smoke-test, and push the runtime base image.

Activating an image for use is a separate step done from the super-admin UI;
this script ONLY publishes pixels into the registry.

Usage:
  scripts/publish-runtime-image.sh                       # build + smoke-test + push
  scripts/publish-runtime-image.sh --no-push             # build + smoke-test only
  scripts/publish-runtime-image.sh --skip-trivy          # skip vulnerability scan
  scripts/publish-runtime-image.sh --skip-smoketest      # skip in-container probe
  scripts/publish-runtime-image.sh --enforce-size-budget # gate on .image-size.last (CI default)

Required (registry.fly.io):
  Fly:ApiToken in System Settings + SystemSettings__EncryptionKey in .env

Optional env:
  REGISTRY                 Default: registry.fly.io
  IMAGE_NAME               Default: glenn-runtime-base
  REGISTRY_USERNAME        For non-Fly registries
  REGISTRY_TOKEN           For non-Fly registries
  PLATFORM                 Default: linux/amd64
  BUILDX_CACHE_FROM        e.g. type=gha,scope=runtime-base (CI sets this)
  BUILDX_CACHE_TO          e.g. type=gha,scope=runtime-base,mode=max
  GITHUB_OUTPUT            CI sets this; script writes tag/digest/size_mb here
HELP
            exit 0
            ;;
        *)
            echo "❌ Unknown flag: $1" >&2
            exit 2
            ;;
    esac
    shift
done

cd "$REPO_ROOT"

# ---------- Compute tag -------------------------------------------------------------
DATE="$(date -u +%Y.%m.%d)"
SHORT_SHA="$(git rev-parse --short=7 HEAD)"
FULL_SHA="$(git rev-parse HEAD)"
TAG="${DATE}-${SHORT_SHA}"
FULL_REF="${REGISTRY}/${IMAGE_NAME}:${TAG}"
LATEST_REF="${REGISTRY}/${IMAGE_NAME}:latest"

echo "🔨 Building $FULL_REF"
echo "   Platform:  $PLATFORM"
echo "   Context:   $REPO_ROOT"
echo "   Push:      $DO_PUSH"
echo "   Trivy:     $DO_TRIVY"
echo "   Smoketest: $DO_SMOKETEST"
echo

# ---------- Registry login (only if pushing) ----------------------------------------
if $DO_PUSH; then
    if [[ "$REGISTRY" == "registry.fly.io" ]]; then
        if [[ -n "${CONTROL_PLANE_PUBLISH_API_KEY:-}" && -n "${CONTROL_PLANE_API:-${API:-}}" ]]; then
            echo "🔑 Logging into registry.fly.io via control plane CI credentials..."
            bash "$REPO_ROOT/scripts/ci/ci-registry-login.sh"
        elif [[ -n "${FLY_API_TOKEN:-}" ]]; then
            echo "🔑 Logging into registry.fly.io with FLY_API_TOKEN..."
            echo "$FLY_API_TOKEN" | docker login registry.fly.io -u x --password-stdin
        else
            echo "🔑 Logging into registry.fly.io with Fly:ApiToken from SystemSettings..."
            FLY_API_TOKEN="$(platform_auth_fly_token)"
            echo "$FLY_API_TOKEN" | docker login registry.fly.io -u x --password-stdin
        fi
    elif [[ -n "${REGISTRY_USERNAME:-}" && -n "${REGISTRY_TOKEN:-}" ]]; then
        echo "🔑 Logging into $REGISTRY with REGISTRY_USERNAME/REGISTRY_TOKEN"
        echo "$REGISTRY_TOKEN" | docker login "$REGISTRY" -u "$REGISTRY_USERNAME" --password-stdin
    else
        echo "ℹ️  No REGISTRY_USERNAME/REGISTRY_TOKEN provided."
        echo "   For registry.fly.io, set Fly:ApiToken in System Settings."
        echo "   Otherwise assume an existing docker login session for $REGISTRY."
    fi
fi

# ---------- Build (load locally so we can smoke-test + measure before push) ---------
# Cache flags are opt-in via env (CI sets them to type=gha; local dev leaves unset).
CACHE_ARGS=()
[[ -n "${BUILDX_CACHE_FROM:-}" ]] && CACHE_ARGS+=( "--cache-from=${BUILDX_CACHE_FROM}" )
[[ -n "${BUILDX_CACHE_TO:-}" ]]   && CACHE_ARGS+=( "--cache-to=${BUILDX_CACHE_TO}" )

# Use buildx if available (needed for GHA cache); fall back to plain `docker build`.
if docker buildx version >/dev/null 2>&1; then
    BUILD_CMD=(docker buildx build --load)
else
    BUILD_CMD=(docker build)
fi

echo "🐳 ${BUILD_CMD[*]}"
"${BUILD_CMD[@]}" \
    --platform "$PLATFORM" \
    --file Dockerfile.runtime-base \
    --tag "$FULL_REF" \
    --tag "$LATEST_REF" \
    ${CACHE_ARGS+"${CACHE_ARGS[@]}"} \
    .

# ---------- Measure size ------------------------------------------------------------
SIZE_BYTES="$(docker image inspect "$FULL_REF" --format '{{.Size}}')"
SIZE_MB=$(( SIZE_BYTES / 1024 / 1024 ))
echo "📏 Image size: ${SIZE_MB} MB"

# ---------- Size budget check -------------------------------------------------------
if $ENFORCE_SIZE_BUDGET; then
    if [[ ! -f "$SIZE_BUDGET_FILE" ]]; then
        echo "ℹ️  No baseline at $SIZE_BUDGET_FILE — establishing one at ${SIZE_MB} MB."
        echo "$SIZE_MB" > "$SIZE_BUDGET_FILE"
    else
        PREV_SIZE_MB="$(cat "$SIZE_BUDGET_FILE")"
        if [[ -z "$PREV_SIZE_MB" || "$PREV_SIZE_MB" -le 0 ]]; then
            echo "ℹ️  Baseline invalid; resetting to ${SIZE_MB} MB."
            echo "$SIZE_MB" > "$SIZE_BUDGET_FILE"
        else
            GROWTH_MB=$(( SIZE_MB - PREV_SIZE_MB ))
            GROWTH_PCT=$(( (GROWTH_MB * 100) / PREV_SIZE_MB ))
            echo "   Previous: ${PREV_SIZE_MB} MB"
            echo "   Current:  ${SIZE_MB} MB"
            echo "   Growth:   ${GROWTH_MB} MB (${GROWTH_PCT}%)"
            if [[ "$GROWTH_PCT" -gt "$SIZE_GROWTH_THRESHOLD_PCT" ]]; then
                COMMIT_MSG="$(git log -1 --pretty=%B)"
                if [[ "$COMMIT_MSG" == *"[image-size-ok]"* ]]; then
                    echo "✅ Override accepted: commit message contains [image-size-ok]."
                else
                    echo "❌ Image grew by ${GROWTH_MB} MB (${GROWTH_PCT}%, threshold ${SIZE_GROWTH_THRESHOLD_PCT}%)." >&2
                    echo "   Justify with [image-size-ok] in commit message or shrink." >&2
                    exit 1
                fi
            fi
            echo "$SIZE_MB" > "$SIZE_BUDGET_FILE"
        fi
    fi
fi

# ---------- Smoke test --------------------------------------------------------------
if $DO_SMOKETEST; then
    echo "🚬 Smoke-testing $FULL_REF"
    docker run --rm "$FULL_REF" bash -c '
        set -e
        echo "--- node ---";       node --version
        echo "--- npm ---";        npm --version
        echo "--- mise ---";       mise --version
        echo "--- cursor-sdk ---"; echo "(bundled in daemon tarball at boot — not in base image)"
        echo "--- gh ---";         gh --version | head -1
        echo "--- git ---";        git --version
        echo "--- supervisord -"; supervisord --version
        echo "--- playwright -";  npx playwright --version
        echo "--- chromium ---";   ls -la /opt/playwright-browsers/chromium*/ | head -3
        echo "--- /opt/agent ---"; ls -la /opt/agent/
        test -x /usr/local/bin/bootstrap-daemon.sh
        test -d /opt/agent
        echo "smoke-test PASSED"
    '
fi

# ---------- Trivy scan --------------------------------------------------------------
if $DO_TRIVY && $DO_PUSH; then
    if command -v trivy >/dev/null 2>&1; then
        echo "🛡️  Trivy scan ($FULL_REF) — OS packages only"
        trivy image \
            --severity CRITICAL \
            --ignore-unfixed \
            --pkg-types os \
            --exit-code 1 \
            "$FULL_REF"
    else
        echo "⚠️  trivy not installed — skipping vulnerability scan."
        echo "   Install with: brew install trivy   (or aquasecurity/trivy on Linux)"
    fi
fi

# ---------- Push --------------------------------------------------------------------
DIGEST=""
if $DO_PUSH; then
    echo "🚀 Pushing $FULL_REF"
    docker push "$FULL_REF"
    echo "🚀 Pushing $LATEST_REF"
    docker push "$LATEST_REF"
    DIGEST="$(docker image inspect "$FULL_REF" --format '{{index .RepoDigests 0}}' | sed 's/.*@//')"
    echo "🔖 Digest: $DIGEST"
fi

# ---------- Outputs (for CI consumption) --------------------------------------------
if [[ -n "${GITHUB_OUTPUT:-}" ]]; then
    {
        echo "tag=${TAG}"
        echo "digest=${DIGEST}"
        echo "size_mb=${SIZE_MB}"
        echo "full_sha=${FULL_SHA}"
    } >> "$GITHUB_OUTPUT"
fi

echo
echo "✅ Done."
echo "   Tag:    $TAG"
echo "   Image:  $FULL_REF"
[[ -n "$DIGEST" ]] && echo "   Digest: $DIGEST"
echo "   Size:   ${SIZE_MB} MB"

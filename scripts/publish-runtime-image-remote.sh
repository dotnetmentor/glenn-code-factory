#!/usr/bin/env bash
# =====================================================================================
# publish-runtime-image-remote.sh
# =====================================================================================
# Build + push the runtime base image WITHOUT a local docker daemon, using Fly's
# remote builder (Depot.dev under the hood). Designed for use from inside the
# agent container, where docker / buildah / podman are all unavailable (user
# namespaces + CAP_SYS_ADMIN are blocked by seccomp).
#
# What it does:
#   1. Installs flyctl into ~/.fly/bin if missing.
#   2. Reads and decrypts Fly:ApiToken from SystemSettings (via .env encryption key).
#   3. Streams the repo as a build context to Fly's remote builder.
#   4. The builder builds Dockerfile.runtime-base and pushes it to
#      registry.fly.io/glenn-runtime-base:<TAG>. Pass --also-latest to
#      also push :latest (slow — flyctl rebuilds remotely; off by default
#      because the API stores sha-pinned refs and doesn't need :latest).
#   5. Registers the new tag in the API's RuntimeImages table and demotes
#      every prior Active row to Deprecated, so RuntimeProvisionerJob picks
#      it up on its next 60s tick. Skip with --no-activate.
#
# Why this exists alongside publish-runtime-image.sh:
#   publish-runtime-image.sh assumes you have a working local docker. CI uses it.
#   Devs with `docker login registry.fly.io` use it. But the agent container has
#   no docker socket and the kernel blocks unprivileged userns, so neither buildah
#   nor podman work either. flyctl talks to Fly's hosted builder over HTTPS,
#   which is the only build path available from inside the agent.
#
# Usage:
#   scripts/publish-runtime-image-remote.sh
#   scripts/publish-runtime-image-remote.sh --also-latest     # also push :latest (slow — rebuilds)
#   scripts/publish-runtime-image-remote.sh --no-activate     # build+push only
#   scripts/publish-runtime-image-remote.sh --tag 2026.05.11-deadbee
#
# Required (local):
#   .env with SystemSettings__EncryptionKey, Jwt__Key, Bootstrap__SuperAdminEmail
#   SystemSettings.Fly:ApiToken populated in Postgres (Super Admin → System Settings)
#   API running at $API (default http://localhost:5338)
#
# Optional env:
#   API               Default: http://localhost:5338
#   PG_URL            Default: postgresql://postgres@localhost:43594/app
#   APP               Default: glenn-runtime-base (Fly app for remote build;
#                     must already exist — script lists your apps if missing).
#   IMAGE_NAME        Default: glenn-runtime-base
#   REGISTRY          Default: registry.fly.io/glenn-runtime-base  — this is
#                     the value stored in RuntimeImages.Registry. It must be
#                     the FULL path because RuntimeProvisionerJob constructs
#                     the Fly image ref as `{Registry}:{Tag}`.
#   PUSH_REGISTRY_HOST  Default: registry.fly.io  — host used for docker login
#                     and the flyctl push target. Just the host, no image name.
#   DOCKERFILE        Default: Dockerfile.runtime-base
#
# Outputs:
#   - Build log: /tmp/fly-runtime-build.log
#   - On success, prints:
#       TAG=<tag>
#       DIGEST=<sha256:...>
#       FULL_REF=registry.fly.io/glenn-runtime-base@sha256:...
#     and if GITHUB_OUTPUT is set, appends them there too.
# =====================================================================================

set -euo pipefail

# NOTE: The `Registry` field stored on RuntimeImages must be the FULL
# `registry.fly.io/<image-name>` path, because RuntimeProvisionerJob constructs
# the Fly image ref as `{Registry}:{Tag}`. If you store only `registry.fly.io`,
# the provisioner will produce `registry.fly.io:<tag>` (treats the tag as a
# port), Fly rejects it with `not a valid image reference`, and the runtime
# transitions Pending → Failed in ~30s. That's why we default REGISTRY here
# to the joined form; the `IMAGE_NAME` variable is only used for naming the
# flyctl app and the tag we push.
REGISTRY="${REGISTRY:-registry.fly.io/glenn-runtime-base}"
IMAGE_NAME="${IMAGE_NAME:-glenn-runtime-base}"
PUSH_REGISTRY_HOST="${PUSH_REGISTRY_HOST:-registry.fly.io}"
APP="${APP:-glenn-runtime-base}"
DOCKERFILE="${DOCKERFILE:-Dockerfile.runtime-base}"
API="${API:-http://localhost:5338}"
REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
# shellcheck source=lib/platform-auth.sh
source "$REPO_ROOT/scripts/lib/platform-auth.sh"

DO_LATEST=false        # API uses sha-pinned refs; :latest is opt-in for humans
DO_ACTIVATE=true
TAG_OVERRIDE=""

while [[ $# -gt 0 ]]; do
    case "$1" in
        --also-latest)   DO_LATEST=true ;;
        --no-activate)   DO_ACTIVATE=false ;;
        --use-db-token)  log "⚠️  --use-db-token is deprecated (DB is now the only token source)" ;;
        --tag)           TAG_OVERRIDE="$2"; shift ;;
        -h|--help)
            # Print the leading comment block (skip shebang, until first non-#-line)
            awk 'NR==1 { next } /^#/ { print; next } { exit }' "$0" | sed 's/^# \{0,1\}//'
            exit 0
            ;;
        *)
            echo "❌ Unknown flag: $1" >&2
            exit 2
            ;;
    esac
    shift
done

log()  { printf '\033[36m[publish-remote]\033[0m %s\n' "$*"; }
fail() { printf '\033[31m[publish-remote FAIL]\033[0m %s\n' "$*" >&2; exit 1; }

# Returns 0 when $1 appears in `fly apps list --json`.
fly_app_exists() {
    local want="$1"
    flyctl apps list --json 2>/dev/null | WANT="$want" python3 -c "
import json, os, sys
want = os.environ['WANT']
try:
    apps = json.load(sys.stdin)
except json.JSONDecodeError:
    sys.exit(2)
for row in apps:
    name = row.get('Name') or row.get('name') or ''
    if name == want:
        sys.exit(0)
sys.exit(1)
" 2>/dev/null
}

fly_list_app_names() {
    flyctl apps list --json 2>/dev/null | python3 -c "
import json, sys
try:
    apps = json.load(sys.stdin)
except json.JSONDecodeError:
    sys.exit(1)
names = sorted({(a.get('Name') or a.get('name') or '') for a in apps} - {''})
for n in names:
    print(n)
" 2>/dev/null || {
        flyctl apps list 2>/dev/null | awk 'NR>1 && $1 !~ /^NAME$/ { print $1 }'
    }
}

# Fail fast with actionable hints instead of a cryptic flyctl deploy error.
fly_app_preflight() {
    local app="$1"
    if fly_app_exists "$app"; then
        log "Fly app \"$app\" found in this account"
        return 0
    fi

    local flyctl_bin="flyctl"
    command -v flyctl >/dev/null 2>&1 || flyctl_bin="$HOME/.fly/bin/flyctl"

    printf '\033[31m[publish-remote FAIL]\033[0m Fly app "%s" does not exist in this account.\n\n' "$app" >&2
    printf '\033[33mApps in this account\033[0m:\n' >&2
    local listed=0 first_app=""
    while IFS= read -r name; do
        [[ -z "$name" ]] && continue
        printf '  • %s\n' "$name" >&2
        [[ -z "$first_app" ]] && first_app="$name"
        listed=1
    done < <(fly_list_app_names || true)
    if [[ "$listed" -eq 0 ]]; then
        printf '  (none listed — run \033[1m%s apps list\033[0m)\n' "$flyctl_bin" >&2
    fi
    printf '\n' >&2
    printf '\033[33mUse an existing app\033[0m (this script uses \033[1mflyctl\033[0m, not the \033[1mfly\033[0m alias):\n' >&2
    if [[ -n "$first_app" ]]; then
        printf '  APP=%s IMAGE_NAME=%s REGISTRY=%s/%s %s\n' \
            "$first_app" "$first_app" "$PUSH_REGISTRY_HOST" "$first_app" "$0" >&2
    else
        printf '  APP=<app-name> IMAGE_NAME=<app-name> REGISTRY=%s/<app-name> %s\n' \
            "$PUSH_REGISTRY_HOST" "$0" >&2
    fi
    printf '\n' >&2
    printf '\033[33mOr create a new app\033[0m (then re-run this script):\n' >&2
    printf '  %s apps create %s\n' "$flyctl_bin" "$app" >&2
    printf '  %s\n' "$0" >&2
    printf '\n' >&2
    printf 'Tip: \033[1mfly\033[0m is often not on PATH; use \033[1m%s\033[0m or re-run this script (it installs flyctl to ~/.fly/bin).\n' \
        "$flyctl_bin" >&2
    exit 1
}

cd "$REPO_ROOT"

# ---------- 1. flyctl install + PATH -----------------------------------------------
# Minimum flyctl version. v0.3.x had a Depot client bug where remote-build pushes
# to registry.fly.io would fail with 401 Unauthorized on intermittent HEAD blob
# checks (`/v2/<image>/blobs/<sha>?ns=registry.fly.io`), aborting the entire
# push even though many layers had already uploaded. Fixed by v0.4.x. Keep this
# floor in lockstep with whatever version we've actually verified end-to-end.
FLYCTL_MIN_VERSION="${FLYCTL_MIN_VERSION:-0.4.50}"

# Compare two semver-ish strings (X.Y.Z, no leading 'v'). Returns 0 if $1 >= $2.
flyctl_version_at_least() {
    local a="$1" b="$2"
    [[ "$(printf '%s\n%s\n' "$a" "$b" | sort -V | head -1)" == "$b" ]]
}

# Add ~/.fly/bin to PATH if a previous install lives there.
[[ -x "$HOME/.fly/bin/flyctl" ]] && export PATH="$HOME/.fly/bin:$PATH"

flyctl_current_version() {
    flyctl version 2>/dev/null | head -1 | grep -oE 'v?[0-9]+\.[0-9]+\.[0-9]+' | head -1 | sed 's/^v//'
}

NEED_INSTALL=false
if ! command -v flyctl >/dev/null 2>&1; then
    NEED_INSTALL=true
    log "flyctl not present, installing..."
else
    CURRENT="$(flyctl_current_version)"
    if [[ -z "$CURRENT" ]] || ! flyctl_version_at_least "$CURRENT" "$FLYCTL_MIN_VERSION"; then
        NEED_INSTALL=true
        log "flyctl $CURRENT is below minimum $FLYCTL_MIN_VERSION, upgrading..."
    fi
fi

if $NEED_INSTALL; then
    curl -fsSL https://fly.io/install.sh | sh >/dev/null
    export PATH="$HOME/.fly/bin:$PATH"
    CURRENT="$(flyctl_current_version)"
    if [[ -z "$CURRENT" ]] || ! flyctl_version_at_least "$CURRENT" "$FLYCTL_MIN_VERSION"; then
        fail "flyctl install/upgrade failed: still at '$CURRENT', need >= $FLYCTL_MIN_VERSION"
    fi
fi

log "flyctl: $(flyctl version | head -1)"

# ---------- 2. Token source (SystemSettings only) ----------------------------------
log "reading Fly:ApiToken from SystemSettings (decrypted via .env key)..."
FLY_API_TOKEN="$(platform_auth_fly_token)" || fail "could not read Fly:ApiToken — set it in Super Admin → System Settings and ensure .env has SystemSettings__EncryptionKey"
export FLY_API_TOKEN

# Verify token works
flyctl auth whoami >/dev/null 2>&1 || fail "FLY_API_TOKEN is not accepted by Fly"

# ---------- 3. Fly app must exist (deploy --build-only targets APP in fly.toml) ---
fly_app_preflight "$APP"

# ---------- 4. Compute tag ---------------------------------------------------------
if [[ -n "$TAG_OVERRIDE" ]]; then
    TAG="$TAG_OVERRIDE"
else
    DATE="$(date -u +%Y.%m.%d)"
    SHORT_SHA="$(git rev-parse --short=7 HEAD)"
    TAG="${DATE}-${SHORT_SHA}"
fi
FULL_REF="${PUSH_REGISTRY_HOST}/${IMAGE_NAME}:${TAG}"
log "target: $FULL_REF"

# ---------- 5. Minimal fly.toml for the build -------------------------------------
# flyctl deploy --build-only needs a config file; resolve dockerfile path relative
# to it, so the toml MUST live next to the Dockerfile (i.e. in the repo root).
TOML="$REPO_ROOT/.fly.runtime-base.toml"
cat > "$TOML" <<EOF
app = "$APP"
primary_region = "iad"

[build]
  dockerfile = "$DOCKERFILE"
EOF
trap 'rm -f "$TOML"' EXIT

# ---------- 6. Remote build + push -------------------------------------------------
log "building remotely (Fly builder)... → $FULL_REF"
log "  build log: /tmp/fly-runtime-build.log"

flyctl deploy \
    --config "$TOML" \
    --dockerfile "$REPO_ROOT/$DOCKERFILE" \
    --image-label "$TAG" \
    --build-only \
    --push \
    --remote-only \
    --no-public-ips \
    2>&1 | tee /tmp/fly-runtime-build.log

# ---------- 7. Extract digest from build log --------------------------------------
# flyctl prints lines like:
#   #18 pushing manifest for registry.fly.io/glenn-runtime-base:TAG@sha256:DIGEST ...
DIGEST="$(grep -oE "${PUSH_REGISTRY_HOST}/${IMAGE_NAME}:${TAG}@sha256:[0-9a-f]{64}" /tmp/fly-runtime-build.log \
            | head -1 | sed -E 's/.*@(sha256:[0-9a-f]+).*/\1/')"
[[ -n "$DIGEST" ]] || fail "couldn't extract digest from build log; check /tmp/fly-runtime-build.log"
FULL_DIGEST_REF="${PUSH_REGISTRY_HOST}/${IMAGE_NAME}@${DIGEST}"
log "digest: $DIGEST"

# Also extract image size (printed by flyctl as `image size: 715 MB`)
SIZE_MB="$(grep -oE 'image size: [0-9]+ MB' /tmp/fly-runtime-build.log | head -1 | grep -oE '[0-9]+')"
[[ -n "$SIZE_MB" ]] || SIZE_MB=0
log "size: ${SIZE_MB} MB"

FULL_SHA="$(git rev-parse HEAD)"
BUILT_AT="$(date -u +%Y-%m-%dT%H:%M:%SZ)"

# ---------- 8. Tag :latest as well (opt-in) ---------------------------------------
# flyctl can only push the labelled tag, so doing :latest means a second remote
# build (slow). Skip by default — the API stores a sha-pinned full ref in the
# RuntimeImages row, so nothing in the runtime path needs :latest.
if $DO_LATEST; then
    log "pushing :latest as alias of :$TAG (this rebuilds remotely) ..."
    flyctl deploy \
        --config "$TOML" \
        --dockerfile "$REPO_ROOT/$DOCKERFILE" \
        --image-label "latest" \
        --build-only \
        --push \
        --remote-only \
        --no-public-ips \
        2>&1 | tee -a /tmp/fly-runtime-build.log >/dev/null || log "⚠️  :latest push failed (non-fatal)"
fi

# ---------- 9. Register + Activate via API ----------------------------------------
if $DO_ACTIVATE; then
    log "minting SuperAdmin JWT from .env + bootstrap user..."
    JWT="$(platform_auth_jwt)" || fail "could not mint JWT — ensure API has bootstrapped SuperAdmin and .env has Jwt__Key + Bootstrap__SuperAdminEmail"

    log "registering in RuntimeImages..."
    REG_BODY="$(python3 -c "
import json
print(json.dumps({
  'tag': '$TAG',
  'digest': '$DIGEST',
  'registry': '$REGISTRY',
  'gitSha': '$FULL_SHA',
  'builtAt': '$BUILT_AT',
  'sizeMb': $SIZE_MB,
  'notes': 'published via publish-runtime-image-remote.sh',
}))")"
    REG_RESP="$(curl -fsS -X POST "$API/api/admin/runtime-images" \
        -H "Authorization: Bearer $JWT" \
        -H "Content-Type: application/json" \
        -d "$REG_BODY")" || fail "register HTTP call failed"
    echo "$REG_RESP" | python3 -m json.tool 2>/dev/null || echo "$REG_RESP"

    IMG_ID="$(echo "$REG_RESP" | python3 -c 'import sys,json; print(json.load(sys.stdin).get("id",""))')"
    [[ -n "$IMG_ID" ]] || fail "API didn't return an image id; check response above"

    # Quirk: POST /api/admin/runtime-images creates the row with Status=Active but
    # does NOT demote any other Active rows. The single-Active invariant is only
    # enforced inside UpdateRuntimeImageStatusHandler when transitioning a row TO
    # Active — and PATCH-ing this brand-new row to Active is a no-op because it's
    # already Active. So to demote prior Active rows we explicitly PATCH them to
    # Deprecated.
    log "demoting prior Active rows (single-Active invariant)..."
    PRIOR_IDS="$(curl -fsS "$API/api/admin/runtime-images?status=Active" \
        -H "Authorization: Bearer $JWT" \
        | python3 -c "
import sys,json
d=json.load(sys.stdin)
for r in d['items']:
    if r['id'] != '$IMG_ID':
        print(r['id'])
")"
    for pid in $PRIOR_IDS; do
        log "  demoting $pid -> Deprecated"
        curl -fsS -X PATCH "$API/api/admin/runtime-images/$pid/status" \
            -H "Authorization: Bearer $JWT" \
            -H "Content-Type: application/json" \
            -d '{"status":"Deprecated"}' >/dev/null
    done
    log "✅ $TAG is the sole Active runtime image."
fi

# ---------- 10. Outputs ------------------------------------------------------------
echo
echo "TAG=$TAG"
echo "DIGEST=$DIGEST"
echo "FULL_REF=$FULL_DIGEST_REF"

if [[ -n "${GITHUB_OUTPUT:-}" ]]; then
    {
        echo "tag=$TAG"
        echo "digest=$DIGEST"
        echo "full_ref=$FULL_DIGEST_REF"
    } >> "$GITHUB_OUTPUT"
fi

log "done."

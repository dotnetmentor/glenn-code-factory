#!/usr/bin/env bash
# Single source of truth for CI publish path filters.
# Keep .github/workflows/publish-daemon.yml and runtime-base-image.yml paths in sync.
#
# GitHub workflow path globs (copy when editing workflows):
#   Daemon:  packages/daemon/**, packages/dotnet-api/Source/Features/SignalR/**,
#            packages/dotnet-api/Source/Features/CiPublish/**,
#            packages/dotnet-api/Source/Features/DaemonVersions/**,
#            scripts/generate-signalr.sh, scripts/publish-daemon.sh,
#            scripts/lib/ci-publish-auth.sh, scripts/ci/**,
#            .github/workflows/publish-daemon.yml
#   Runtime: Dockerfile.runtime-base, docker/**,
#            packages/dotnet-api/Source/Features/CiPublish/**,
#            packages/dotnet-api/Source/Features/RuntimeImages/**,
#            scripts/publish-runtime-image.sh, scripts/publish-runtime-image-remote.sh,
#            scripts/ci/**, .image-size.last, .github/workflows/runtime-base-image.yml

matches_daemon_publish_path() {
  local path="$1"
  [[ "$path" == packages/daemon/* ]] && return 0
  [[ "$path" == packages/dotnet-api/Source/Features/SignalR/* ]] && return 0
  [[ "$path" == packages/dotnet-api/Source/Features/CiPublish/* ]] && return 0
  [[ "$path" == packages/dotnet-api/Source/Features/DaemonVersions/* ]] && return 0
  [[ "$path" == scripts/generate-signalr.sh ]] && return 0
  [[ "$path" == scripts/publish-daemon.sh ]] && return 0
  [[ "$path" == scripts/lib/ci-publish-auth.sh ]] && return 0
  [[ "$path" == scripts/ci/* ]] && return 0
  [[ "$path" == .github/workflows/publish-daemon.yml ]] && return 0
  return 1
}

matches_runtime_publish_path() {
  local path="$1"
  [[ "$path" == Dockerfile.runtime-base ]] && return 0
  [[ "$path" == docker/* ]] && return 0
  [[ "$path" == packages/dotnet-api/Source/Features/CiPublish/* ]] && return 0
  [[ "$path" == packages/dotnet-api/Source/Features/RuntimeImages/* ]] && return 0
  [[ "$path" == scripts/publish-runtime-image.sh ]] && return 0
  [[ "$path" == scripts/publish-runtime-image-remote.sh ]] && return 0
  [[ "$path" == scripts/ci/* ]] && return 0
  [[ "$path" == .image-size.last ]] && return 0
  [[ "$path" == .github/workflows/runtime-base-image.yml ]] && return 0
  return 1
}

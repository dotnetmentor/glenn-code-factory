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
#   Runtime template (Box): docker/**, scripts/build-box-template.sh,
#            packages/dotnet-api/Source/Features/CiPublish/**,
#            packages/dotnet-api/Source/Features/RuntimeTemplates/**, scripts/ci/**
#   NOTE: template builds need a live Box account, so there is no CI workflow for
#   them today — an operator runs scripts/build-box-template.sh. The matcher stays
#   so a future workflow (or a CI notice job) can reuse it.

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
  [[ "$path" == docker/* ]] && return 0
  [[ "$path" == packages/dotnet-api/Source/Features/CiPublish/* ]] && return 0
  [[ "$path" == packages/dotnet-api/Source/Features/RuntimeTemplates/* ]] && return 0
  [[ "$path" == scripts/build-box-template.sh ]] && return 0
  [[ "$path" == scripts/ci/* ]] && return 0
  return 1
}

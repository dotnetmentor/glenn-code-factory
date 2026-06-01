#!/usr/bin/env bash
set -euo pipefail

REPO="/data/project/repo"
API_DIR="$REPO/packages/dotnet-api"

export DOTNET_ROOT="${DOTNET_ROOT:-${HOME}/.dotnet}"
export PATH="${DOTNET_ROOT}:${DOTNET_ROOT}/tools:/usr/share/dotnet:/usr/bin:/bin"
export DATABASE_URL="${DATABASE_URL:-Host=localhost;Port=5432;Database=app;Username=postgres;Password=postgres}"
export ASPNETCORE_ENVIRONMENT="${ASPNETCORE_ENVIRONMENT:-Development}"

cd "$API_DIR"

# Idempotent — safe on every supervisord restart.
dotnet restore --nologo -v q
exec dotnet run --no-launch-profile --urls http://0.0.0.0:5338

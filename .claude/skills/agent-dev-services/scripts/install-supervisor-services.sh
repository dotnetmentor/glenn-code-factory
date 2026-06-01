#!/usr/bin/env bash
# Install persistent dotnet-api + backoffice-web supervisord programs on the agent runtime.
set -euo pipefail

REPO="/data/project/repo"
SKILL="$REPO/.claude/skills/agent-dev-services"
SUPERVISOR_D="/data/.glenn/supervisor.d"

echo "[agent-dev-services] ensuring PostgreSQL (system, port 5432)..."
if ! pg_isready -h 127.0.0.1 -p 5432 >/dev/null 2>&1; then
  sudo service postgresql start
  sleep 2
fi
if ! pg_isready -h 127.0.0.1 -p 5432 >/dev/null 2>&1; then
  echo "ERROR: PostgreSQL is not accepting connections on 127.0.0.1:5432" >&2
  exit 1
fi

echo "[agent-dev-services] ensuring app database..."
if ! sudo -u postgres psql -tAc "SELECT 1 FROM pg_database WHERE datname='app'" | grep -q 1; then
  sudo -u postgres createdb app
fi

echo "[agent-dev-services] installing frontend deps (dev)..."
(cd "$REPO/packages/backoffice-web" && NODE_ENV=development npm install --no-fund --no-audit)

echo "[agent-dev-services] restoring API + applying migrations..."
export DATABASE_URL="${DATABASE_URL:-Host=localhost;Port=5432;Database=app;Username=postgres;Password=postgres}"
export ASPNETCORE_ENVIRONMENT=Development
export DOTNET_ROOT="${DOTNET_ROOT:-${HOME}/.dotnet}"
export PATH="${DOTNET_ROOT}:${DOTNET_ROOT}/tools:/usr/share/dotnet:/usr/bin:/bin"
(cd "$REPO/packages/dotnet-api" && dotnet restore --nologo)
if command -v dotnet-ef >/dev/null 2>&1; then
  (cd "$REPO/packages/dotnet-api" && dotnet ef database update)
else
  echo "WARN: dotnet-ef not installed — run: dotnet tool install --global dotnet-ef" >&2
fi

chmod +x "$SKILL/scripts/run-dotnet-api.sh" "$SKILL/scripts/run-backoffice-web.sh"

mkdir -p "$SUPERVISOR_D"
install -m 644 "$SKILL/templates/dotnet-api.conf" "$SUPERVISOR_D/dotnet-api.conf"
install -m 644 "$SKILL/templates/backoffice-web.conf" "$SUPERVISOR_D/backoffice-web.conf"

echo "[agent-dev-services] registering with supervisord..."
supervisorctl reread
supervisorctl update
supervisorctl start dotnet-api backoffice-web || true
supervisorctl status dotnet-api backoffice-web

echo
echo "API:  http://localhost:5338  (swagger: /swagger)"
echo "Web:  http://localhost:5173"
echo "Logs: /var/log/supervisor/dotnet-api.{out,err}.log"
echo "      /var/log/supervisor/backoffice-web.{out,err}.log"

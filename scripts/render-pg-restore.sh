#!/usr/bin/env bash
# Restore a custom-format pg_dump into a (usually new, empty) Render Postgres database.
#
# Typical use: Oregon backup → new Frankfurt orchestrator-db after Blueprint deploy.
#
# Usage:
#   export DATABASE_URL='postgresql://user:pass@dpg-….frankfurt-postgres.render.com/app'
#   ./scripts/render-pg-restore.sh .render-backups/render-20260602-120000.dump
#
# Or restore the newest dump in .render-backups/:
#   export DATABASE_URL='postgresql://…'
#   ./scripts/render-pg-restore.sh
#
# Get DATABASE_URL from Render → new orchestrator-db → Connect → External Database URL.
# Stop the Oregon API (or enable maintenance) before the final restore if you need zero drift.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
BACKUP_DIR="${RENDER_BACKUP_DIR:-$ROOT/.render-backups}"
DUMP_FILE="${1:-${DUMP_FILE:-}}"

if [[ -z "${DATABASE_URL:-}" ]]; then
  echo "ERROR: DATABASE_URL is not set (target database — usually new Frankfurt instance)." >&2
  echo "  export DATABASE_URL='postgresql://…'" >&2
  exit 1
fi

if ! command -v pg_restore >/dev/null 2>&1; then
  echo "ERROR: pg_restore not found. Install PostgreSQL client tools (e.g. brew install libpq)." >&2
  exit 1
fi

if [[ -z "$DUMP_FILE" ]]; then
  if [[ ! -d "$BACKUP_DIR" ]]; then
    echo "ERROR: No dump path given and $BACKUP_DIR does not exist." >&2
    echo "Usage: $0 path/to/render-YYYYMMDD-HHMMSS.dump" >&2
    exit 1
  fi
  DUMP_FILE="$(ls -t "$BACKUP_DIR"/render-*.dump 2>/dev/null | head -1 || true)"
  if [[ -z "$DUMP_FILE" ]]; then
    echo "ERROR: No render-*.dump files in $BACKUP_DIR" >&2
    exit 1
  fi
  echo "Using latest dump: $DUMP_FILE"
fi

if [[ ! -f "$DUMP_FILE" ]]; then
  echo "ERROR: Dump file not found: $DUMP_FILE" >&2
  exit 1
fi

echo "Restoring into target from DATABASE_URL (host hidden)."
echo "  dump:   $DUMP_FILE"
echo "  flags:  --clean --if-exists --no-owner --no-acl"
echo ""
echo "Press Enter to continue, or Ctrl-C to abort."
read -r _

# pg_restore may exit 1 with harmless warnings (e.g. extensions). Capture and report.
set +e
pg_restore \
  --dbname="$DATABASE_URL" \
  --verbose \
  --clean \
  --if-exists \
  --no-owner \
  --no-acl \
  "$DUMP_FILE"
RESTORE_EXIT=$?
set -e

echo ""
if [[ $RESTORE_EXIT -eq 0 ]]; then
  echo "Restore finished successfully."
else
  echo "pg_restore exited with code $RESTORE_EXIT."
  echo "If the only errors were about existing objects or extensions, inspect the log above;"
  echo "then verify with: psql \"\$DATABASE_URL\" -c '\\dt public.*'"
  exit "$RESTORE_EXIT"
fi

echo ""
echo "Next steps:"
echo "  1. Point orchestrator-api (Frankfurt) DATABASE_URL at this database (Blueprint fromDatabase does this)."
echo "  2. Copy env secrets from Oregon service → Frankfurt service."
echo "  3. Deploy / smoke-test, then move factory.glenncode.ai to the new API."
echo "  4. Retire Oregon stack when satisfied."

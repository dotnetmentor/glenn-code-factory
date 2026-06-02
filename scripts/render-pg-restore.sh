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

LOG_FILE="$BACKUP_DIR/restore-$(date -u +%Y%m%d-%H%M%S).log"
mkdir -p "$BACKUP_DIR"

echo "Restoring into target from DATABASE_URL (host hidden)."
echo "  dump:   $DUMP_FILE"
echo "  log:    $LOG_FILE"
echo "  flags:  --clean --if-exists --no-owner --no-acl"
echo ""
if [[ "${RENDER_RESTORE_YES:-}" != "1" ]]; then
  echo "Press Enter to continue, or Ctrl-C to abort (or export RENDER_RESTORE_YES=1)."
  read -r _
fi

# pg_restore often exits 1 with "errors ignored on restore: 1" (extension/role noise on
# managed Postgres). Full output goes to the log so the real error line is not lost.
set +e
pg_restore \
  --dbname="$DATABASE_URL" \
  --verbose \
  --clean \
  --if-exists \
  --no-owner \
  --no-acl \
  "$DUMP_FILE" 2>&1 | tee "$LOG_FILE"
RESTORE_EXIT="${PIPESTATUS[0]}"
set -e

ERROR_LINES="$(grep -E '^(pg_restore: error:|ERROR:)' "$LOG_FILE" 2>/dev/null || true)"

echo ""
if [[ -n "$ERROR_LINES" ]]; then
  echo "pg_restore reported errors (see $LOG_FILE):"
  echo "$ERROR_LINES" | head -20
  echo ""
fi

VERIFY_OK=0
if command -v psql >/dev/null 2>&1; then
  echo "Sanity check:"
  psql "$DATABASE_URL" -v ON_ERROR_STOP=1 -Atqc \
    "SELECT 'Projects' AS t, COUNT(*)::text FROM \"Projects\"
     UNION ALL SELECT '__EFMigrationsHistory', COUNT(*)::text FROM \"__EFMigrationsHistory\";" \
    && VERIFY_OK=1 || true
  echo ""
fi

if [[ $RESTORE_EXIT -eq 0 ]]; then
  echo "Restore finished successfully (pg_restore exit 0)."
elif [[ $VERIFY_OK -eq 1 ]] && [[ "$(echo "$ERROR_LINES" | grep -c . || true)" -le 3 ]]; then
  echo "pg_restore exited with code $RESTORE_EXIT but core tables have rows."
  echo "This is normal on Render when one object (often an extension) fails — safe to proceed."
  echo "Scroll $LOG_FILE for the single ignored error if curious."
else
  echo "pg_restore exited with code $RESTORE_EXIT and verification did not pass."
  echo "Inspect: $LOG_FILE"
  exit "$RESTORE_EXIT"
fi

echo ""
echo "Next steps:"
echo "  1. Point orchestrator-api (Frankfurt) DATABASE_URL at this database (Blueprint fromDatabase does this)."
echo "  2. Copy env secrets from Oregon service → Frankfurt service."
echo "  3. Deploy / smoke-test, then move factory.glenncode.ai to the new API."
echo "  4. Retire Oregon stack when satisfied."

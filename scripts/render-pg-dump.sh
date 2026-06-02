#!/usr/bin/env bash
# Dump a Render Postgres database to a local file (for region migration or backup).
#
# Usage:
#   export DATABASE_URL='postgresql://user:pass@dpg-….oregon-postgres.render.com/app'
#   ./scripts/render-pg-dump.sh
#
# Get the URL from Render Dashboard → orchestrator-db → Connect → External Database URL.
# Use the external URL from your laptop (not the internal URL, which only works inside Render).
# Prefer the direct / unpooled URL if Render shows both (pg_dump over pooler can fail).
#
# Output: .render-backups/render-YYYYMMDD-HHMMSS.dump (custom format for pg_restore)
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
OUT_DIR="${RENDER_BACKUP_DIR:-$ROOT/.render-backups}"
STAMP="$(date -u +%Y%m%d-%H%M%S)"
OUT_FILE="$OUT_DIR/render-${STAMP}.dump"

if [[ -z "${DATABASE_URL:-}" ]]; then
  echo "ERROR: DATABASE_URL is not set." >&2
  echo "Copy External Database URL from Render → orchestrator-db → Connect, then:" >&2
  echo "  export DATABASE_URL='postgresql://…'" >&2
  exit 1
fi

if ! command -v pg_dump >/dev/null 2>&1; then
  echo "ERROR: pg_dump not found. Install PostgreSQL client tools (e.g. brew install libpq)." >&2
  exit 1
fi

mkdir -p "$OUT_DIR"

echo "Dumping to $OUT_FILE"
echo "  (schema: public, format: custom/-Fc, suitable for pg_restore)"

pg_dump \
  -Fc \
  -v \
  --no-owner \
  --no-acl \
  --schema=public \
  -d "$DATABASE_URL" \
  -f "$OUT_FILE"

BYTES="$(wc -c <"$OUT_FILE" | tr -d ' ')"
echo ""
echo "Done. $(du -h "$OUT_FILE" | awk '{print $1}') ($BYTES bytes)"
echo "Restore into Frankfurt (new DB external URL):"
echo "  export DATABASE_URL='postgresql://…'   # new orchestrator-db"
echo "  ./scripts/render-pg-restore.sh \"$OUT_FILE\""

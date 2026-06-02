#!/usr/bin/env bash
set -euo pipefail

# Default dev stack: persistent Cloudflare quick tunnel → local API (5338) + Vite frontend.
# Exports Runtime__PublicApiUrl so Fly runtimes can dial back (overrides DB + .env).
# The tunnel survives npm run dev restarts; stop it with: npm run dev:tunnel:stop

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
API_PORT="${DEV_API_PORT:-5338}"

_tunnel_out="$(bash "$ROOT/scripts/dev-tunnel.sh" ensure)"
TUNNEL_MODE="$(printf '%s\n' "$_tunnel_out" | sed -n '1p')"
PUBLIC_URL="$(printf '%s\n' "$_tunnel_out" | sed -n '2p')"
if [[ -z "$PUBLIC_URL" ]]; then
  PUBLIC_URL="$TUNNEL_MODE"
  TUNNEL_MODE="STARTED"
fi
export Runtime__PublicApiUrl="$PUBLIC_URL"

REUSED=""
if [[ "$TUNNEL_MODE" == "REUSED" ]]; then
  REUSED=" (reused existing tunnel — quit dev does not stop it)"
fi

echo ""
echo "════════════════════════════════════════════════════════════════════"
echo "  npm run dev"
echo "  Frontend:  http://localhost:5173"
echo "  API:       http://127.0.0.1:${API_PORT}"
echo "  Fly URL:   $PUBLIC_URL  (Runtime__PublicApiUrl)${REUSED}"
echo ""
echo "  Stop tunnel: npm run dev:tunnel:stop"
echo "  Respawn Fly runtimes only when the Fly URL changes."
echo "════════════════════════════════════════════════════════════════════"
echo ""

cd "$ROOT"
dotenv -e .env -- concurrently -n "API,WEB" -c "blue,magenta" \
  "npm run api:watch" \
  "npm run web:dev"

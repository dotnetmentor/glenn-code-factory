#!/bin/bash
set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT_DIR="$(dirname "$SCRIPT_DIR")"

echo "🔧 Building dotnet-api (triggers TypedSignalR analyzers)..."
cd "$ROOT_DIR/packages/dotnet-api"
dotnet build

# Generate twice — once for the React frontend (existing consumer) and once
# for the daemon (Card 2 of the daemon-codegen migration). Same dotnet tsrts
# invocation, two output dirs, two consumers. Wiping each dir before
# regenerating avoids stale generated files surviving a contract change.

FRONTEND_OUTPUT_DIR="$ROOT_DIR/packages/backoffice-web/src/generated/signalr"
DAEMON_OUTPUT_DIR="$ROOT_DIR/packages/daemon/src/generated/signalr"

echo "📝 Generating TypeScript SignalR clients (frontend)..."
rm -rf "$FRONTEND_OUTPUT_DIR"
mkdir -p "$FRONTEND_OUTPUT_DIR"
dotnet tsrts \
  --project api.csproj \
  --output "$FRONTEND_OUTPUT_DIR" \
  --enum Name

echo "📝 Generating TypeScript SignalR clients (daemon)..."
rm -rf "$DAEMON_OUTPUT_DIR"
mkdir -p "$DAEMON_OUTPUT_DIR"
dotnet tsrts \
  --project api.csproj \
  --output "$DAEMON_OUTPUT_DIR" \
  --enum Name

echo "✅ TypeScript SignalR clients generated at:"
echo "   $FRONTEND_OUTPUT_DIR"
echo "   $DAEMON_OUTPUT_DIR"

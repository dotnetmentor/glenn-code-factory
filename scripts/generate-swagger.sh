#!/bin/bash
set -e

echo "🔨 Generating Swagger JSON..."

# Port for swagger generation (default 5339 to avoid conflicts with main API on 5338)
SWAGGER_API_PORT=${SWAGGER_API_PORT:-5339}
echo "📡 Using port: $SWAGGER_API_PORT"

# Kill any existing dotnet processes on the swagger port
lsof -ti:$SWAGGER_API_PORT | xargs kill -9 2>/dev/null || true

# Start the API in the background with swagger generation mode on dedicated port
cd ./packages/dotnet-api
if [ -f ../../.env ]; then
  set -a
  # shellcheck disable=SC1091
  source ../../.env
  set +a
fi
export Jwt__Key="${Jwt__Key:-swagger-export-only-jwt-key-min-32-chars!!}"
SWAGGER_GENERATION_MODE=true dotnet run --no-build --urls "http://localhost:$SWAGGER_API_PORT" &
API_PID=$!

# Function to cleanup on exit
cleanup() {
    echo "🧹 Cleaning up..."
    kill $API_PID 2>/dev/null || true
    lsof -ti:$SWAGGER_API_PORT | xargs kill -9 2>/dev/null || true
}
trap cleanup EXIT

# Wait for API to be ready (max 30 seconds)
echo "⏳ Waiting for API to start..."
for i in {1..30}; do
    if curl -s http://localhost:$SWAGGER_API_PORT/swagger/v1/swagger.json > /dev/null 2>&1; then
        echo "✅ API is ready!"
        break
    fi
    if [ $i -eq 30 ]; then
        echo "❌ API failed to start within 30 seconds"
        exit 1
    fi
    sleep 1
done

# Fetch the swagger JSON
echo "📥 Fetching Swagger JSON..."
curl -s http://localhost:$SWAGGER_API_PORT/swagger/v1/swagger.json -o ../../swagger.json

echo "✅ Swagger JSON generated at swagger.json"


cd ../../packages/backoffice-web
npx orval
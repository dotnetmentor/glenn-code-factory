#!/usr/bin/env bash
set -euo pipefail

REPO="/data/project/repo"
WEB_DIR="$REPO/packages/backoffice-web"

cd "$WEB_DIR"

# NODE_ENV=production in the agent shell omits devDependencies — force dev for Vite.
export NODE_ENV=development

if [[ ! -d node_modules/vite ]]; then
  npm install --no-fund --no-audit
fi

exec ./node_modules/.bin/vite --host 0.0.0.0 --port 5173

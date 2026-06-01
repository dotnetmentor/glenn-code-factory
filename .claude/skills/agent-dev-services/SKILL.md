---
name: agent-dev-services
description: Start and persist the local .NET API and React frontend on the managed agent runtime (supervisord, system Postgres on 5432). Use when (1) starting backend/frontend for local dev on the agent machine, (2) services died after an agent turn, (3) user asks to run npm run dev or dotnet run persistently, (4) wiring API to non-Docker Postgres on this environment.
---

# Agent Dev Services (Managed Runtime)

Background shell processes **die when an agent turn ends**. Use **supervisord** drop-ins under `/data/.glenn/supervisor.d/` so the API and frontend survive across turns.

## Quick start

```bash
bash .claude/skills/agent-dev-services/scripts/install-supervisor-services.sh
```

This will:

1. Start **system PostgreSQL 15** on `127.0.0.1:5432` (not Docker)
2. Ensure database `app` exists
3. `npm install` (with `NODE_ENV=development`) + `dotnet restore` + migrations
4. Register `dotnet-api` and `backoffice-web` with supervisord
5. Start both services

## Endpoints

| Service | URL |
|---------|-----|
| Frontend | http://localhost:5173 |
| API | http://localhost:5338 |
| Swagger | http://localhost:5338/swagger |

Vite proxies `/api` and `/hubs` → `5338` (see `packages/backoffice-web/vite.config.ts`).

## Database (non-Docker)

Uses the **Debian system Postgres**, not `packages/local-db` Docker compose:

```
Host=localhost;Port=5432;Database=app;Username=postgres;Password=postgres
```

Passed via `DATABASE_URL` in the supervisord program env (overrides `appsettings.json`).

First boot seeds dev users (OTP/password `Test123!`):

- `admin@test.com` — OTP `111111`
- `test@test.com` — OTP `123456`

## Supervisord commands

```bash
supervisorctl status dotnet-api backoffice-web
supervisorctl restart dotnet-api
supervisorctl restart backoffice-web
supervisorctl tail -f dotnet-api stderr
supervisorctl tail -f backoffice-web stderr
```

Logs on disk:

- `/var/log/supervisor/dotnet-api.out.log`
- `/var/log/supervisor/backoffice-web.out.log`

## Do NOT use for persistence

```bash
# Dies when the agent turn ends:
dotnet run &
npm run dev &
```

## Prerequisites on a fresh machine

| Tool | Install (if missing) |
|------|----------------------|
| .NET 9 SDK | `sudo apt install dotnet-sdk-9.0` |
| PostgreSQL 15 | `sudo apt install postgresql` |
| EF migrations tool | `dotnet tool install --global dotnet-ef` |

## `NODE_ENV=production` gotcha

The agent shell sets `NODE_ENV=production`, which skips frontend **devDependencies**. The install script and run wrappers force `NODE_ENV=development` for Vite.

## Files

| Path | Purpose |
|------|---------|
| `scripts/install-supervisor-services.sh` | One-shot install + start |
| `scripts/run-dotnet-api.sh` | Supervisord command for API |
| `scripts/run-backoffice-web.sh` | Supervisord command for Vite |
| `templates/*.conf` | Copied to `/data/.glenn/supervisor.d/` |

## Uninstall

```bash
supervisorctl stop dotnet-api backoffice-web
rm /data/.glenn/supervisor.d/dotnet-api.conf /data/.glenn/supervisor.d/backoffice-web.conf
supervisorctl reread && supervisorctl update
```

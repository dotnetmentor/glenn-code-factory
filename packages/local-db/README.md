# Local Database Package

PostgreSQL 16 in Docker for local development.

## Quick start

From the **repo root** (recommended):

```bash
npm run db:up      # start container + wait until ready
npm run db:down    # stop container
npm run db:logs    # follow logs
npm run db:restart # down + up
```

Or from this directory:

```bash
npm start    # docker compose up -d && wait-for-db
npm stop
npm run logs
npm run destroy   # stop and delete volume (fresh DB)
```

## Connection details

Matches `docker-compose.yml` and root `.env.example`:

| Setting  | Value      |
|----------|------------|
| Host     | localhost  |
| Port     | **43594**  |
| Database | app        |
| User     | postgres   |
| Password | *(none — trust auth inside container)* |

Connection string:

```
Host=localhost;Port=43594;Database=app;Username=postgres
```

The .NET API reads this via `DATABASE_URL` in your root `.env` file.

## What's included

- PostgreSQL 16 (`postgres:16`)
- Persistent Docker volume (`postgres_agent_data`)
- Health checks
- Port **43594** mapped to container 5432 (avoids clashing with a local Postgres on 5432)

## Requirements

- Docker Desktop or Docker Engine with Compose v2

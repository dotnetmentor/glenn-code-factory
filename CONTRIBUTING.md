# Contributing to GlennCode Factory

Thanks for your interest in contributing! This guide covers how to get a local
environment running, the conventions we follow, and how to get a change merged.

## Getting started

See the [README](README.md) for full setup. The short version:

```bash
cp .env.example .env          # fill SystemSettings__EncryptionKey, Jwt__Key, Bootstrap__SuperAdminEmail
npm run setup                 # Postgres + restore + migrations
npm run dev                   # API + frontend (+ Cloudflare quick tunnel)
```

| Tool | Version |
|------|---------|
| .NET SDK | 9.x |
| Node.js | 20+ |
| Docker | for local Postgres |

## Project layout

- `packages/dotnet-api` — .NET 9 Web API (vertical slices + CQRS). See [`packages/dotnet-api/CLAUDE.md`](packages/dotnet-api/CLAUDE.md) for backend conventions.
- `packages/backoffice-web` — React 19 + MUI frontend. API hooks are **generated** (Orval) — never hand-edit `src/api/`.
- `packages/daemon` — agent runtime (Cursor SDK, SignalR, bootstrap).
- `packages/e2e` — Playwright end-to-end tests.

## Before you open a PR

Run the same checks CI runs:

```bash
# Backend
cd packages/dotnet-api && dotnet build          # must be 0 errors
cd packages/dotnet-api && dotnet test           # (Tests project)

# Frontend
cd packages/backoffice-web && npm run typecheck
cd packages/backoffice-web && npm run build
```

If you changed an API controller, regenerate the client so frontend types stay in sync:

```bash
npm run generate-swagger
```

If you changed a SignalR hub contract:

```bash
npm run generate-signalr
```

## Conventions

- **Controllers must return typed responses** — Swagger/Orval codegen depends on it.
- **Result pattern** for business outcomes; exceptions only for technical failures.
- **Domain events** are raised on entities, not published manually. See `.claude/skills/domain-events`.
- **Never commit secrets.** `appsettings.json` and `.env.example` hold empty placeholders only; real values go in `.env` (gitignored) or System Settings.
- Match the style of the surrounding code.

## Commit & PR

1. Branch off `main`.
2. Keep PRs focused; describe what changed and why.
3. Make sure CI is green (build, typecheck, tests).
4. Link any related issue.

## Reporting bugs / requesting features

Use the [issue templates](.github/ISSUE_TEMPLATE). For security issues, **do not**
open a public issue — see [SECURITY.md](SECURITY.md).

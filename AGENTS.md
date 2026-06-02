# Agent Instructions

> Shared instructions for AI coding agents (Claude Code, Cursor, and others).
> `CLAUDE.md` imports this file so both toolchains read the same guidance.

You are a combination of Jony Ive and Steve Jobs—you create beautiful, valuable, and working software.

**Your users may be non-technical.** They have ideas, not code. You are their bridge to magic.

---

## ⛔ MANDATORY SUBAGENT DELEGATION

**Delegate. Do not fix bugs yourself.**

| Trigger | Action | Example |
|---------|--------|---------|
| Build error, type error, exception | `@debugger` | "error CS0246", "Cannot find name" |
| "not working", "bug", "fix" | `@debugger` | User reports something broken |
| Plan, design, spec | `@planning` | "let's design the feature" |
| "review", "check code", "audit" | `@reviewer` | Before PR |

**@debugger** — For ANY bug, error, or issue
**@planning** — For planning/design
**@reviewer** — Pragmatic code review (flags real issues: manual types, missing Orval hooks, untyped controllers)

---

## Platform agents (managed runtime)

Agents running inside a project's Fly runtime get their operating instructions from
the daemon harness (`packages/daemon/src/harness/harness.md`) — git handling, the
spec → accept → kanban workflow, and runtime tools are defined there, not in this
file. This document is for local development with Claude Code, Cursor, and similar
tools; commit and PR conventions for contributors live in [`CONTRIBUTING.md`](CONTRIBUTING.md).

---

## Build Workflow

1. **Plan first** — Delegate to `@planning` to gather requirements
2. **Backend first, then frontend** — Create backend features before frontend
3. **Use generated types** — Never write manual API types
4. **Verify before finishing** — See Build Verification below

---

## Environment

Paths below use the managed runtime root. Locally, substitute your clone path.

| Service | Location | Port |
|---------|----------|------|
| PostgreSQL | localhost | 43594 (platform) / 5432 (local Docker) |
| .NET API | `/data/project/repo/packages/dotnet-api` | 5338 |
| React Dev | `/data/project/repo/packages/backoffice-web` | 5173 |
| Cloudflare Tunnel | — | Exposes frontend to user's browser |

**Secrets:** Never commit real credentials. `appsettings.json` and `.env.example` use empty placeholders; supply values via environment variables (`Section__Key` in `.env` or your host's env config). See `.env.example` for the full list.

---

## Skills Reference

When the user needs specific functionality, use these skills from `.claude/skills/`
(this is the canonical skills location for this repo):

| Need | Skill | Key Points |
|------|-------|------------|
| **File Upload** | `file-upload` | R2 (prod) / Local (dev), switchable |
| **Real-time** | `signalr` | Live collaboration, presence |
| **PDF Generation** | `pdf` | Client-side with @react-pdf/renderer |
| **Charts** | `charts` | Recharts data visualization |
| **Maps** | `map` | Mapbox GL (react-map-gl) |
| **Domain Events/DDD** | `domain-events` | Rich entities, auto-dispatched events, event store traceability |
| **Code Review** | `code-review` | Find real issues: manual types, missing hooks, untyped controllers |
| **MUI / theme** | `instrument-mui` | Instrument Mono design system, workspace tokens |
| **MUI styling choices** | `material-ui-styling` | sx vs styled vs theme overrides |
| **E2E tests** | `e2e-testing` | Playwright POMs, web-first assertions |
| **Cursor SDK** | `cursor-sdk` | Building on `@cursor/sdk` |
| **Frontend design** | `frontend-design` | Distinctive, production-grade UI |
| **Browser automation** | `agent-browser` | Drive a browser as an agent |
| **Stack patterns** | `tech-stack` | .NET 9 + React 19 conventions |
| **Local dev services** | `agent-dev-services` | Run API + web on the runtime |
| **Authoring a skill** | `skill-creator` | Create / update skills |

### Platform / infrastructure skills

Shipping daemon or runtime infrastructure changes? Read the skill — do not guess at the publish sequence:

| Skill | Use when |
|-------|----------|
| `daemon-deploy` | Anything under `packages/daemon/` changed, or the RuntimeHub / SignalR contract changed → rebuild & republish the daemon bundle |
| `runtime-deployment` | Shipping a new runtime base image, provisioning runtimes, or diagnosing stuck `Bootstrapping` / `Online` states |
| `runtime-debug` | SSH into a Fly Machine, read daemon logs, hot-swap bundle, recover from FATAL |
| `runtime-environment` | Full runtime architecture map (daemon, supervisord, Fly volume, SignalR hub, persistence) |
| `self-healing-runtime` | Degraded Online boot, SpecHealth, "Let agent fix it" repair loop |

---

## Frontend API (Orval)

Auto-generated React Query hooks from Swagger. Generate with `./scripts/generate-swagger.sh`

```tsx
import { useGetApiProjects, usePostApiProjects, getGetApiProjectsQueryKey } from '../api/queries-commands'

// Query
const { data } = useGetApiProjects()

// Mutation
const createMutation = usePostApiProjects()
createMutation.mutate({ data: { name: 'New Project' } })

// Invalidate after mutation
queryClient.invalidateQueries({ queryKey: getGetApiProjectsQueryKey() })
```

**Naming patterns:**
- `useGetApi{Entity}` — List all
- `useGetApi{Entity}Id` — Get by ID
- `usePostApi{Entity}` — Create
- `usePutApi{Entity}Id` — Update
- `useDeleteApi{Entity}Id` — Delete

API types and React Query hooks are **auto-generated** — never edit `packages/backoffice-web/src/api/` by hand.

---

## Backend Architecture

See [`packages/dotnet-api/CLAUDE.md`](packages/dotnet-api/CLAUDE.md) for:
- Vertical slice + CQRS patterns
- Command/Query handlers
- Result pattern (no exceptions)
- Domain events
- Controller return types (required for Swagger)

---

## Quick Reference

**Add file upload:**
1. Read `.claude/skills/file-upload/SKILL.md`
2. Inject `IFileStorageService`
3. Use `SaveFileAsync()` / `GetFileUrlAsync()`

**Review code before PR:**
1. Delegate to `@reviewer` subagent
2. Catches: manual types, missing Orval hooks, untyped controllers, missing invalidation

---

## Build Verification

**ALWAYS verify changes compile before finishing:**

| What | Command | Location |
|------|---------|----------|
| Frontend typecheck | `npm run typecheck` | `packages/backoffice-web` |
| Frontend full build | `npm run build` | `packages/backoffice-web` |
| Backend build | `dotnet build` | `packages/dotnet-api` |

**❌ NEVER use:**
- `npx tsc --noEmit` (picks up wrong TypeScript compiler)
- `tsc` directly (same issue)

**✅ ALWAYS use the npm scripts:**
```bash
# Frontend - from packages/backoffice-web
npm run typecheck   # Quick type check
npm run build       # Full build

# Backend - from packages/dotnet-api
dotnet build
```

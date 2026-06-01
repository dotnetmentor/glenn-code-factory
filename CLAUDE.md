# Agent Instructions

## ⛔ CRITICAL: GIT IS AUTOMATIC — NEVER COMMIT *(managed platform only)*

**On the managed agent platform**, the harness commits AND pushes after every turn. Do not run `git add`, `git commit`, or `git push`. Do not ask the user "want me to commit these?" — the answer is always no, because it's already going to happen.

- ✅ Leave uncommitted changes alone when you finish — the harness handles it.
- ✅ Run `git status` / `git diff` / `git log` freely for inspection.
- ❌ Never offer to commit "verification fixes" or "leftover files."
- ❌ Never run `git commit` even if a subagent's output suggests it.

**Exception:** Only run write-side git commands if the user explicitly says "commit this" or "push now."

This applies to EVERY subagent (@debugger, @backend, @frontend, @planning, @reviewer) — if you're reading this file, the rule is yours.

---

## ⛔ CRITICAL: MANDATORY SUBAGENT DELEGATION

**YOU MUST DELEGATE. DO NOT FIX BUGS YOURSELF.**

| Trigger | Action | Example |
|---------|--------|---------|
| Build error, type error, exception | `@debugger` | "error CS0246", "Cannot find name" |
| "not working", "bug", "fix" | `@debugger` | User reports something broken |
| Plan, design, spec | `@planning` | "let's design the feature" |
| "review", "check code", "audit" | `@reviewer` | Before PR |

---

You are a combination of Jony Ive and Steve Jobs—you create beautiful, valuable, and working software.

**Your users may be non-technical.** They have ideas, not code. You are their bridge to magic.

---

## Platform Workflow

On the managed runtime, follow the harness workflow (injected at turn start from `packages/daemon/src/harness/`):

1. **Write a spec** — Delegate to `@planning` for features or multi-step changes. Specs are saved via MCP (`mcp__specifications__*`) and visible in the user's Specs tab.
2. **Wait for acceptance** — After `@planning` finishes, stop and wait for the user to say "go" before creating kanban cards or writing code.
3. **Kanban + execute** — Read the accepted spec, create cards via `mcp__kanban__*`, drive them across the board (one In Progress at a time).

**Task tracking:** Use kanban MCP tools for anything that should survive the turn or be visible to the user. Do not use in-session todo lists.

**Daemon tools:** `propose_runtime_spec` (add/change runtime services) and `restart_service` (restart supervisord services) are available in-process on the platform.

For trivial one-turn requests (typo fix, single-line edit, answering a question), skip the spec stage and just do the work.

---

## Environment

Paths below use the managed runtime root. Locally, substitute your clone path.

| Service | Location | Port |
|---------|----------|------|
| PostgreSQL | localhost | 43594 (platform) / 5432 (local Docker) |
| .NET API | `/data/project/repo/packages/dotnet-api` | 5338 |
| React Dev | `/data/project/repo/packages/backoffice-web` | 5173 |
| Cloudflare Tunnel | — | Exposes frontend to user's browser |

---

## Platform Deployment

When shipping daemon or runtime infrastructure changes, read these skills — do not guess at the publish sequence:

| Skill | Use when |
|-------|----------|
| `.claude/skills/daemon-deploy/SKILL.md` | Anything under `packages/daemon/` changed, or RuntimeHub / SignalR contract changed → rebuild & republish daemon bundle via `./scripts/publish-daemon.sh` |
| `.claude/skills/runtime-deployment/SKILL.md` | Shipping a new runtime base image (`Dockerfile.runtime-base`), provisioning runtimes, or diagnosing stuck `Bootstrapping` / `Online` states |
| `.claude/skills/runtime-debug/SKILL.md` | SSH into a Fly Machine, read daemon logs, hot-swap bundle, recover from FATAL |
| `.claude/skills/runtime-environment/SKILL.md` | Full runtime architecture map (daemon, supervisord, Fly volume, SignalR hub, persistence) |
| `.claude/skills/self-healing-runtime/SKILL.md` | Degraded Online boot, SpecHealth, "Let agent fix it" repair loop |

---

## Subagents

**@debugger** — For ANY bug, error, or issue
**@planning** — For planning/design (specs via MCP on platform; local mirror at `.sdd/specifications/` if present)
**@reviewer** — Pragmatic code review (flags real issues: manual types, missing Orval hooks, untyped controllers)

---

## Build Workflow

1. **Plan first** - Delegate to `@planning` to gather requirements
2. **Backend first, then frontend** - Create backend features before frontend
3. **Use generated types** - Never write manual API types
4. **Don't commit** - See the CRITICAL git banner at the top of this file

---

## Skills Reference

When the user needs specific functionality, use these skills from `.claude/skills/`:

| Need | Skill | Key Points |
|------|-------|------------|
| **AI Chat/Assistant** | `openrouter` | SSE streaming, class-based tools, minimal code |
| **File Upload** | `file-upload` | R2 (prod) / Local (dev), switchable |
| **AI Image Gen** | `gemini-image` | Generation + object replacement |
| **Real-time** | `signalr` | Live collaboration, presence |
| **PDF Generation** | `pdf` | Client-side with @react-pdf/renderer |
| **Rich Text** | `rich-text` | TipTap editor |
| **Charts** | `charts` | Recharts data visualization |
| **Drag & Drop** | `drag-drop` | dnd-kit sortable lists |
| **Excel Import** | `excel` | ExcelJS client-side parsing |
| **3D Graphics** | `threejs` | React Three Fiber |
| **Email Builder** | `email-builder` | Drag-drop email templates |
| **Maps** | `map` | Mapbox GL (react-map-gl) |
| **Domain Events/DDD** | `domain-events` | Rich entities, auto-dispatched events, event store traceability |
| **Code Review** | `code-review` | Find real issues: manual types, missing hooks, untyped controllers |
| **MUI / theme** | `instrument-mui` | Instrument Mono design system, workspace tokens |
| **Daemon publish** | `daemon-deploy` | SignalR contract changed → `./scripts/publish-daemon.sh` + `./scripts/generate-signalr.sh` |
| **Runtime deploy** | `runtime-deployment` | Base image, Fly provisioning, smoke tests |
| **Runtime debug** | `runtime-debug` | SSH, logs, hot-swap, OOM/FATAL recovery |
| **Runtime architecture** | `runtime-environment` | Fly machine, bootstrap, spec, SignalR, persist_rootfs |
| **Self-healing specs** | `self-healing-runtime` | Degraded Online, repair loop, auto-apply consent |

**Secrets:** Never commit real credentials. `appsettings.json` and `.env.example` use empty placeholders; supply values via environment variables (`Section__Key` in `.env` or your host's env config). See `.env.example` for the full list.

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

---

## Backend Architecture

See `packages/dotnet-api/CLAUDE.md` for:
- Vertical slice + CQRS patterns
- Command/Query handlers
- Result pattern (no exceptions)
- Domain events
- Controller return types (required for Swagger)

---

## Quick Reference

**Add AI chat feature:**
1. Read `.claude/skills/openrouter/SKILL.md`
2. Backend: Create streaming endpoint using `StreamAsSseAsync()`
3. Frontend: Use `useOpenRouterChat` hook

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

# Spec: Claude Agent SDK backend (multi-backend, model + reasoning switching)

Status: Draft → in build
Author: agent (William)
Date: 2026-06-23

## 1. Goal

Re-introduce a **Claude** coding-agent backend alongside the existing **Cursor**
backend, implemented competently this time:

- Backend selectable **per conversation** (Cursor or Claude), with a config default.
- **Model switching** within the Claude backend (Opus 4.8 / Sonnet 4.6 / Haiku 4.5 / Fable 5).
- **Reasoning-level switching** (effort: low / medium / high / xhigh / max) — first-class, not a hidden token budget.
- **Smooth streaming** — text + thinking + tool deltas render incrementally and correctly (thinking never leaks into the answer).
- **Maximum reuse** of today's frontend components and the existing daemon ↔ server contract.

Non-goals (this round): the OpenCode universal gateway, Bedrock/Vertex/Foundry routing,
Claude Code OAuth-token auth (API-key BYOK only to start).

## 2. Why this is tractable

The daemon already isolates the agent backend behind one seam:

```ts
// packages/daemon/src/turn/AgentFactory.ts
export type AgentFactory = (opts: TurnOptions) => AsyncIterable<TurnEvent>
```

`buildCursorFactory()` is the sole implementation today. The Claude Agent SDK
(`@anthropic-ai/claude-agent-sdk`) maps almost 1:1 onto the Cursor SDK calls
already in `CursorFactory.ts`, and its event stream (`assistant` text/thinking/
tool_use blocks + terminal `result` with usage) maps cleanly onto the existing
`TurnEvent` frames. So a Claude backend is a **parallel factory + parallel event
mapper**, with no change to the SignalR `TurnEvent` model or the frontend chat
stream.

We have prior art: the codebase previously supported three backends
(`claude`, `opencode`, `cursor`) via an `AgentBackend` discriminator, per-backend
model catalogs, BYOK columns, and a `ReasoningOptions` jsonb. The Cursor-only
migration (`20260525114005_CursorOnlyAgentBackend.cs`) collapsed it; its `Down()`
is a complete snapshot of the old schema and is the reference for the new one.

## 3. Architecture

```
StartTurn(payload: { ..., backend?, model?, reasoningEffort? })
        │
   TurnRunner ── selects AgentFactory by payload.backend (default from DaemonConfig)
        │
        ├── buildCursorFactory  → @cursor/sdk      → CursorEventMapper  ┐
        └── buildClaudeFactory  → @anthropic-ai/   → ClaudeEventMapper  ├─► TurnEvent ─► EmitEvent (unchanged)
                                   claude-agent-sdk                     ┘
```

The `TurnEvent` shape, SignalR `EmitEvent` payload, `TurnRunner` emit loop,
auto-commit/git hooks, and the frontend chat renderer are **unchanged**.

## 4. Work breakdown (phased)

### Phase 1 — Daemon Claude backend (streaming core) ✅ first

Files (new unless noted):
- `packages/daemon/src/turn/ClaudeFactory.ts` — `buildClaudeFactory(deps): AgentFactory`.
  - Dynamic `import('@anthropic-ai/claude-agent-sdk')` (mirror Cursor's lazy import).
  - Maps `TurnOptions` → SDK `query()` options: `model`, `resume`/session, `cwd`,
    `mcpServers`, `permissionMode` (from `yolo`), `effort` (reasoning),
    `thinking: { type: 'adaptive' }` on reasoning-capable models.
  - Resume resilience: pass `resume: sessionId`; on "session not found" fall back to a
    fresh `query()` with the harness preamble (same pattern as `CursorFactory`).
  - Captures the SDK `session_id` (from `system.init`) so the hub can persist/resume.
- `packages/daemon/src/turn/ClaudeEventMapper.ts` — SDK message stream → `TurnEvent`.
  - `system.init` → capture session id (no user-facing frame, or a System frame).
  - `assistant` blocks: `text` → `AssistantText` (coalesced deltas for smooth streaming),
    `thinking` → **`Thinking`** (never `AssistantText`), `tool_use` → `ToolUse` (with
    lifecycle/dedup parity to `CursorEventMapper`).
  - `tool_result` → tool-result frame; `result` → terminal `Status` + `RunResultPayload`
    (durationMs, model, usage/cost, artifacts/git).
- `packages/daemon/src/turn/AgentFactory.ts` — unchanged (seam already generic).
- `packages/daemon/src/turn/TurnOptions.ts` — add `backend?: 'cursor' | 'claude'` and
  `reasoningEffort?: 'low'|'medium'|'high'|'xhigh'|'max'`.
- `packages/daemon/src/turn/TurnRunner.ts` — read `backend`/`reasoningEffort` from payload;
  select factory (injected map of factories) instead of a single `cursorFactory`.
- `packages/daemon/src/main.ts` — build both factories, pass a `{ cursor, claude }` map;
  default backend from config.
- `packages/daemon/src/config/DaemonConfig.ts` — `defaultBackend` (env `AGENT_BACKEND`,
  default `cursor`), Claude default model.
- `packages/daemon/src/signalr/types.ts` — `AgentSecretsDto.anthropicApiKey: string | null`.
- `packages/daemon/package.json` — add `@anthropic-ai/claude-agent-sdk`.

Streaming-smoothness requirements (mirror `CursorEventMapper`):
- Coalesce consecutive `text_delta`s into `AssistantText` increments; flush on block stop.
- Keep thinking deltas in a separate `Thinking` stream so the UI shows a thinking
  block, not answer text. (This was the exact bug the old `ReasoningOptions`
  migration warned about.)
- Preserve tool-call dedup and status-frame buffering already done for Cursor.

Verify: `npm run build` + `npm run typecheck` in `packages/daemon`. Behind a flag
(`AGENT_BACKEND=cursor` default) so nothing changes until opted in.

### Phase 2 — .NET schema + API

- EF migration: add `AgentBackend` discriminator (`varchar(32)`, default `cursor`)
  to the relevant project/conversation/session tables (reference the old
  `Down()` snapshot).
- `ClaudeModels` catalog table: `slug`, `displayName`, `isSystemDefault`,
  `supportsReasoning` (bool), `defaultEffort`, soft-delete tombstone — mirror the
  shape/patterns of `CursorModels`. Seed Opus 4.8 (default), Sonnet 4.6, Haiku 4.5, Fable 5.
- BYOK: `EncryptedAnthropicApiKey` column + `/byok` payload field
  (`setAnthropicApiKey` / `anthropicApiKey`), mirroring the old controller surface.
- Persist `reasoningEffort` + Claude resume/session id per conversation.
- Secret resolution returns `anthropicApiKey` to the daemon (extend `GetSecrets`).
- Vertical-slice handlers + typed controller return types (Swagger), per backend CLAUDE.md.

Verify: `dotnet build` in `packages/dotnet-api`.

### Phase 3 — SignalR contract + daemon republish

- Add `backend?` and `reasoningEffort?` to `StartTurnPayload`; add `anthropicApiKey`
  to the secrets contract.
- `./scripts/generate-signalr.sh` to regenerate the daemon's typed client.
- Rebuild + republish the daemon bundle (`./scripts/publish-daemon.sh`) — contract
  changes require it or fresh runtimes fail bootstrap (see daemon-deploy skill).

### Phase 4 — Frontend (reuse-first)

- Regenerate Orval hooks (`./scripts/generate-swagger.sh`) for new model/BYOK endpoints.
- **Reuse** the existing per-conversation override pattern (`useAgentModelOverride`):
  generalize to `{ backend, model, reasoningEffort }` (one localStorage record, or
  promote to server-persisted conversation settings).
- Composer UI: add a **backend** selector + **reasoning** dropdown next to the existing
  model picker, reusing the same dropdown/menu components already in the chat composer
  (`DemoChat`/workspace chat). When backend = Claude, the model list comes from
  `ClaudeModels`; reasoning dropdown shown only when the chosen model `supportsReasoning`.
- BYOK: extend the existing BYOK/credentials surface with an Anthropic API key field
  (reuse the `OptionalSecret` set/clear pattern).
- Chat stream rendering: **no change** — Thinking/AssistantText/ToolUse frames already
  render; we only ensure the mapper emits them.

Verify: `npm run typecheck` + `npm run build` in `packages/backoffice-web`.

## 5. Models & reasoning (authoritative)

- Aliases: `opus`→`claude-opus-4-8`, `sonnet`→`claude-sonnet-4-6`,
  `haiku`→`claude-haiku-4-5`, `fable`→`claude-fable-5`.
- Reasoning = SDK `effort` (`low|medium|high|xhigh|max`) + adaptive thinking on
  Opus 4.7/4.8 & Fable 5. Do **not** use `budget_tokens` (400 on those models).
- UI reasoning options: Low / Medium / High / Max (xhigh optional power-user).
- Default per model lives in `ClaudeModels.defaultEffort`; per-conversation override wins.

## 6. Risks / gotchas

- **Thinking rendering** — map `thinking` blocks to `Thinking`, never answer text.
- **Resume on ephemeral runtimes** — local SDK session files vanish on respawn;
  keep `.NET DB` as source of truth + create-fallback (parity with Cursor).
- **Dep install** — `@anthropic-ai/claude-agent-sdk` must be present in the runtime
  base image; dynamic import keeps the daemon building if types are absent at compile.
- **Contract discipline** — any `StartTurnPayload`/secrets change ⇒ regen + republish daemon.
- **Auth** — API key BYOK first; OAuth-token path deferred.

## 7. Rollout

Ship behind `AGENT_BACKEND` default `cursor`. Enable Claude per conversation once
Phase 4 lands. Phase 1 is independently verifiable (daemon builds, flag-gated) and
carries no behavior change until selected.

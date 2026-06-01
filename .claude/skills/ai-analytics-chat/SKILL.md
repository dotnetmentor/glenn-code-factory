---
name: ai-analytics-chat
description: >
  Build an AI-powered analytics chat panel that lets users query their database
  using natural language. The AI writes and executes SQL queries, presents results
  as tables, and maintains conversation history. Uses .NET backend with OpenRouter.NET
  for LLM streaming (SSE), class-based tools for schema discovery and query execution,
  and React frontend with conversation persistence, markdown rendering, and compact
  tool-call visualization. Use when: (1) Adding an AI analytics/chat feature that
  queries a database, (2) Building a natural language to SQL interface, (3) Creating
  an AI assistant panel with tool use and streaming, (4) Setting up conversational
  AI with persistent history and multi-tenant scoping.
---

# AI Analytics Chat

AI chat panel where users ask questions about their data in natural language.
The AI discovers the database schema, writes SQL, executes queries, and presents results —
all streamed via SSE with tool-call visualization.

## Architecture

```
User question → SSE stream → LLM → Tool calls → SQL execution → Streamed response
                                ↓
                    1. get_business_overview (structured summary)
                    2. get_schema (discover tables/columns)
                    3. execute_query (SQL → results for LLM analysis)
                    4. save_and_present_query (SQL → queryId → frontend renders table)
```

**Key decisions:**
- LLM writes SQL dynamically — scales to any schema without code changes
- Schema discovery via tool call, not bloated system prompt
- Conversations persisted by `conversationId` — frontend never sends message arrays
- Tool calls rendered as grouped compact chips (e.g. "Schema ×3")
- Two-field messages: `Content` (for LLM) + `BlocksJson` (for frontend rendering)

## Implementation Steps

### 1. Backend: Security Layer

Three-layer defense for LLM-generated SQL. See [references/backend.md](references/backend.md).

- **SqlValidationService** — table whitelist, block DML/DDL, block sensitive columns, require `@organisationId`
- **TenantScopedQueryExecutor** — auto-bind org param, 10s statement timeout, row limits
- **SchemaDiscoveryService** — expose only allowed tables with scoping notes

### 2. Backend: LLM Tools

Four `Tool<TParams, TResult>` classes:

| Tool | Purpose | LLM sees result? |
|------|---------|-------------------|
| `get_business_overview` | Structured org summary (no SQL needed) | Yes |
| `get_schema` | Table/column discovery | Yes |
| `execute_query` | Run SQL, return max 100 rows | Yes |
| `save_and_present_query` | Validate SQL, save, return queryId | No (just ID) |

`save_and_present_query` is the key pattern: validates with `LIMIT 1`, saves to DB, returns `queryId`. Frontend fetches and renders the data separately — LLM context stays small.

### 3. Backend: System Prompt

Must include domain knowledge, not just SQL rules. See [references/system-prompt.md](references/system-prompt.md).

Essential sections: domain model explanation, table relationships, business logic, SQL rules (PascalCase, tenant scoping, soft-delete), tool usage guide, error recovery instructions, key column names for common tables.

### 4. Backend: Streaming & Conversations

SSE endpoint + conversation CRUD. See [references/backend.md](references/backend.md).

```
POST /chat/stream  →  { message, conversationId, model }
```

Backend loads history from DB when `conversationId` provided. Frontend only sends the new message.

CRUD: list, create, delete (cascade), get messages, append messages. Auto-title from first user message.

### 5. Frontend: Chat View

See [references/frontend.md](references/frontend.md).

- Load DB messages on mount, reconstruct `ChatMessage[]` from `BlocksJson`
- Parent keys component by `conversationId` → re-mounts on switch
- `onCompleted` saves new messages (both `Content` and `BlocksJson`)
- `initialMessage` prop for suggestion clicks
- Slow-stream warning (20s) with cancel button

### 6. Frontend: Tool Call Visualization

Group consecutive tool calls into compact horizontal chips:

```
Before: [Schema] [Schema] [Schema] [Query] [Query]  (vertical, bloated)
After:  [Schema ×3] [Query ×2]  (one row, compact)
```

`buildSegments()` preprocesses blocks into: text → markdown, table → QueryDataTable, tools → grouped chips with spinning/checkmark icons.

### 7. Frontend: Markdown + Conversations

- `react-markdown` + `remark-gfm` with custom MUI components
- `ConversationPicker` dropdown in header
- `conversationId` persisted in localStorage
- Stale ID cleanup on initial load only (not reactive — avoids race condition)

## Pitfalls

1. **New conversation race condition**: Stale-ID cleanup effect fires before conversation list refetches → clears new ID. Fix: run cleanup once on mount, not reactively.

2. **Column hallucination**: LLM guesses wrong column names despite system prompt. Fix: explicit "key column names" section + error recovery instruction ("call get_schema, don't guess").

3. **`hasLoadedHistory` must be state, not ref**: Refs don't trigger re-renders → dependent effects never fire.

4. **Slow streams feel broken**: AI retrying bad SQL looks like hanging. Fix: 20s timer → warning + cancel button.

## References

- [references/backend.md](references/backend.md) — Entity models, tools, security, controller patterns
- [references/frontend.md](references/frontend.md) — Chat view, tool visualization, conversation management
- [references/system-prompt.md](references/system-prompt.md) — Domain-aware prompt design guide

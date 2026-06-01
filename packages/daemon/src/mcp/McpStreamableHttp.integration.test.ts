// Daemon-side E2E integration test for the MCP Streamable HTTP transport.
// Spec: mcp-streamable-http-transport, Card 80a4d975.
//
// ──────────────────────────────────────────────────────────────────────────────
// WHAT THIS TEST CATCHES THAT UNIT TESTS CAN'T
// ──────────────────────────────────────────────────────────────────────────────
// `CursorFactory.test.ts` already pins the *construction* shape — that we
// build `mcpServers` with `type: 'http'`, a base URL, and a Bearer header. It
// does NOT exercise the live handshake. This file does:
//
//   1. The base URL is actually mounted as a JSON-RPC dispatcher (not the
//      legacy REST-only shape, which used to return 405 Method Not Allowed
//      when POSTed at the base path — that's what we used to ship and that's
//      why the Cursor SDK MCP client saw an empty tools list).
//   2. The JWT issued for this runtime authenticates against the dispatcher
//      (right scheme, right claims, right scope).
//   3. The `tools/list` response carries the platform tools the SDK will
//      surface to the model as `mcp__specifications__*` and `mcp__kanban__*`.
//
// ──────────────────────────────────────────────────────────────────────────────
// WHY THIS IS NOT A FULL SDK BOOT
// ──────────────────────────────────────────────────────────────────────────────
// The card's ideal shape is "boot the SDK, capture the first SystemMessage
// with subtype: init, assert the tools array". That requires a real Cursor
// API key, a real model round-trip, and a real Cursor agent — none of which
// we want on every CI run, even guarded.
//
// What actually changes between "the SDK sees the tools" and "the JSON-RPC
// `tools/list` returns the tools" is zero — the SDK *only* learns about a
// server's tools by making this exact call (per MCP Streamable HTTP spec, rev
// 2025-06-18, §3.4 "Tools"). If `tools/list` returns the right catalog with a
// real bearer token, the SDK's init `SystemMessage` will list them. If it
// doesn't, no SDK configuration will paper over it.
//
// So this test is the *minimum sufficient* live verification. The full SDK
// boot is reserved for the operational smoke surface (manual: ship a fresh
// daemon bundle to a runtime, dispatch a prompt, eyeball the init message in
// the timeline UI). That smoke is gated on having an Online runtime, which is
// an env concern this file doesn't try to own.
//
// ──────────────────────────────────────────────────────────────────────────────
// HOW TO RUN
// ──────────────────────────────────────────────────────────────────────────────
//   INTEGRATION_BACKEND_URL=http://localhost:5338 \
//   INTEGRATION_RUNTIME_TOKEN=<a runtime JWT> \
//   npm test -- McpStreamableHttp.integration
//
// Both env vars must be set, otherwise every `it.runIf` skips. CI defaults to
// skipped — wire a job in only when an integration backend + minted token are
// available.
//
// To get a runtime token: either pull one from the daemon's `auth-token.json`
// on a running runtime, or call `RuntimeTokenService.MintAsync` directly via
// a test seam. The token must include `rt_runtime`, `rt_project`, `rt_tenant`
// claims (RuntimeTokenAuthenticationDefaults.SchemeName JWT).
//
// ──────────────────────────────────────────────────────────────────────────────
// FAILURE MODE DOCUMENTED (would have failed against the old REST-only shape)
// ──────────────────────────────────────────────────────────────────────────────
// Pre-spec: POST /api/mcp/kanban/v1 with any JSON body returned HTTP 405 "Method
// Not Allowed" (only /api/mcp/kanban/v1/{method} routes existed). The SDK's MCP
// Streamable HTTP client treats that as "server unreachable" and silently
// emits an empty tools list at init. Symptom in production: SystemMessage.init
// shows only `mcp__glenn-daemon-tools__*`, never `mcp__kanban__*` or
// `mcp__specifications__*`. To confirm a regression locally:
//
//   1. Comment out the `[HttpPost("")] HandleJsonRpc` action on McpControllerBase.
//   2. Re-run this test against the rebuilt backend.
//   3. Expect: `it('returns a tool catalog from /mcp/kanban/v1')` fails with
//      `Method Not Allowed` (status 405) or a body shaped `{ "error": { "code":
//      -32601 } }` if some other handler is mounted.

import { describe, expect, it } from 'vitest'

// ─── Test gating ──────────────────────────────────────────────────────────────
// `it.runIf(condition)` is vitest's first-class skip-when-falsy. We compute the
// gate once at file-load so a missing env var skips every test, not just the
// first to read it.
const BACKEND_URL = process.env['INTEGRATION_BACKEND_URL']
const RUNTIME_TOKEN = process.env['INTEGRATION_RUNTIME_TOKEN']
const INTEGRATION_ENABLED = Boolean(BACKEND_URL && RUNTIME_TOKEN)

// ─── JSON-RPC helpers ─────────────────────────────────────────────────────────
// We hand-roll the JSON-RPC envelope here rather than reusing daemon code so
// the test is independent of the daemon's MCP client implementation — if the
// daemon's client drifts, this test still pins the *protocol* contract.

type JsonRpcResponse =
  | {
      jsonrpc: '2.0'
      id: number | string | null
      result: unknown
    }
  | {
      jsonrpc: '2.0'
      id: number | string | null
      error: { code: number; message: string; data?: unknown }
    }

async function postJsonRpc(
  baseUrl: string,
  body: { jsonrpc: '2.0'; id: number; method: string; params?: unknown },
  token: string,
): Promise<{ status: number; body: JsonRpcResponse }> {
  const res = await fetch(baseUrl, {
    method: 'POST',
    headers: {
      'Content-Type': 'application/json',
      Accept: 'application/json',
      Authorization: `Bearer ${token}`,
    },
    body: JSON.stringify(body),
  })
  // Per JSON-RPC 2.0, success and error envelopes are both HTTP 200. Anything
  // else (404 missing route, 401 bad token, 405 wrong-method) is a transport-
  // layer regression we want the test to surface verbatim.
  const text = await res.text()
  let parsed: JsonRpcResponse
  try {
    parsed = JSON.parse(text) as JsonRpcResponse
  } catch {
    throw new Error(
      `POST ${baseUrl} returned status ${res.status} with non-JSON body: ${text.slice(0, 500)}`,
    )
  }
  return { status: res.status, body: parsed }
}

interface ToolDescriptor {
  name: string
  description: string
  inputSchema: { type: string; properties?: Record<string, unknown>; required?: string[] }
}

function expectSuccess(
  envelope: JsonRpcResponse,
): asserts envelope is Extract<JsonRpcResponse, { result: unknown }> {
  if ('error' in envelope) {
    throw new Error(
      `Expected JSON-RPC success but got error: code=${envelope.error.code} message=${envelope.error.message}`,
    )
  }
}

// ─── Tests ────────────────────────────────────────────────────────────────────

describe('MCP Streamable HTTP — live backend handshake', () => {
  it.runIf(INTEGRATION_ENABLED)(
    'kanban v1 /api/mcp/kanban/v1 returns tool catalog via JSON-RPC tools/list',
    async () => {
      const url = `${BACKEND_URL}/api/mcp/kanban/v1`
      const { status, body } = await postJsonRpc(
        url,
        { jsonrpc: '2.0', id: 1, method: 'tools/list' },
        RUNTIME_TOKEN!,
      )
      // 405 / 404 here is the pre-spec regression signature (REST-only shape).
      expect(status, `POST ${url} should be JSON-RPC 200, not transport error`).toBe(200)
      expectSuccess(body)

      const result = body.result as { tools: ToolDescriptor[] }
      expect(Array.isArray(result.tools)).toBe(true)
      const names = result.tools.map((t) => t.name).sort()

      // These are the four MCP method names the kanban controller mounts via
      // [HttpPost("...")] — see KanbanMcpController.cs. If new tools are added,
      // bump this list. The check is `toContain` not `toEqual` so the test
      // doesn't fail-closed on additive changes.
      for (const expected of ['createCard', 'listCards', 'getCard', 'updateCard']) {
        expect(names, `kanban tools/list missing "${expected}"`).toContain(expected)
      }

      // Every tool must ship a JSON Schema input so the SDK can synthesise
      // arguments. This is the contract the model relies on.
      for (const tool of result.tools) {
        expect(tool.inputSchema, `tool "${tool.name}" must have inputSchema`).toBeDefined()
        expect(tool.inputSchema.type).toBe('object')
      }
    },
  )

  it.runIf(INTEGRATION_ENABLED)(
    'specifications v1 /api/mcp/specifications/v1 returns tool catalog via JSON-RPC tools/list',
    async () => {
      const url = `${BACKEND_URL}/api/mcp/specifications/v1`
      const { status, body } = await postJsonRpc(
        url,
        { jsonrpc: '2.0', id: 2, method: 'tools/list' },
        RUNTIME_TOKEN!,
      )
      expect(status).toBe(200)
      expectSuccess(body)

      const result = body.result as { tools: ToolDescriptor[] }
      const names = result.tools.map((t) => t.name).sort()
      for (const expected of [
        'saveSpecification',
        'readSpecification',
        'listSpecifications',
        'deleteSpecification',
      ]) {
        expect(names, `specifications tools/list missing "${expected}"`).toContain(expected)
      }
    },
  )

  it.runIf(INTEGRATION_ENABLED)(
    'kanban v1 initialize on /api/mcp/kanban/v1 returns protocol version 2025-06-18 with tools capability',
    async () => {
      const url = `${BACKEND_URL}/api/mcp/kanban/v1`
      const { status, body } = await postJsonRpc(
        url,
        {
          jsonrpc: '2.0',
          id: 3,
          method: 'initialize',
          params: {
            protocolVersion: '2025-06-18',
            capabilities: {},
            clientInfo: { name: 'daemon-integration-test', version: '0.0.0' },
          },
        },
        RUNTIME_TOKEN!,
      )
      expect(status).toBe(200)
      expectSuccess(body)

      const result = body.result as {
        protocolVersion: string
        serverInfo: { name: string; version: string }
        capabilities: { tools?: { listChanged?: boolean } }
      }
      expect(result.protocolVersion).toBe('2025-06-18')
      expect(result.serverInfo.name).toBe('kanban')
      expect(result.capabilities.tools).toBeDefined()
    },
  )

  it.runIf(INTEGRATION_ENABLED)(
    'unknown JSON-RPC method on a valid server returns MethodNotFound (-32601), not HTTP error',
    async () => {
      // Verifies the dispatcher handles bogus methods gracefully — the SDK
      // shouldn't be able to crash the controller by sending nonsense.
      const url = `${BACKEND_URL}/api/mcp/kanban/v1`
      const { status, body } = await postJsonRpc(
        url,
        { jsonrpc: '2.0', id: 4, method: 'no/such/method' },
        RUNTIME_TOKEN!,
      )
      expect(status).toBe(200) // JSON-RPC convention: error in body, HTTP 200.
      if ('result' in body) {
        throw new Error('expected JSON-RPC error response, got result')
      }
      expect(body.error.code).toBe(-32601)
    },
  )

  // Sanity check that the gate works — runs unconditionally so CI surfaces
  // misconfiguration ("you set BACKEND_URL but not RUNTIME_TOKEN" stays
  // observable). Doesn't hit the backend, just asserts the env shape.
  it('integration gate is honored — both env vars required', () => {
    if (BACKEND_URL && !RUNTIME_TOKEN) {
      throw new Error(
        'INTEGRATION_BACKEND_URL is set but INTEGRATION_RUNTIME_TOKEN is missing. ' +
          'Either set both or neither.',
      )
    }
    if (RUNTIME_TOKEN && !BACKEND_URL) {
      throw new Error(
        'INTEGRATION_RUNTIME_TOKEN is set but INTEGRATION_BACKEND_URL is missing. ' +
          'Either set both or neither.',
      )
    }
    // If neither is set, this is fine — integration tests are CI-opt-in.
    expect(INTEGRATION_ENABLED).toBe(Boolean(BACKEND_URL && RUNTIME_TOKEN))
  })
})

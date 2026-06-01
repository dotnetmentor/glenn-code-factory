// Tests for `propose_runtime_spec` — runtime-spec-v3-presets.
//
// V3 contract: the daemon does NOT enforce a structural schema on the spec it
// forwards. Description + JSON schema are fetched from the backend at startup
// (see `fetchToolDescription.ts`) and the tool body just forwards
// `{ proposedSpec, reason }` to `POST /api/runtimes/{id}/proposals`. The backend
// owns preset enumeration, parameter validation, and template expansion.
//
// This file covers the HTTP wiring only — it doesn't care about the V3 spec
// shape because the daemon doesn't either:
//   1. Happy path — POST to /api/runtimes/{id}/proposals with the forwarded body.
//   2. 4xx with `{ error: '...' }` → ProposalSendError carrying that code.
//   3. 4xx with non-JSON body → ProposalSendError(`proposal_rejected_<status>`).
//   4. 5xx → ProposalSendError(`proposal_send_failed`).
//   5. Network error → ProposalSendError(`proposal_send_failed`).
//   6. Token rotation — auth header is resolved at call time.
//   7. Trailing slash on mainApiUrl is normalised.
//   8. Bare envelope validation — missing proposedSpec / reason is rejected
//      locally without hitting the network.
//   9. Description + inputSchema flow through verbatim from the deps.

import { describe, expect, it, vi } from 'vitest'
import type { Logger } from 'pino'

import type { DaemonConfig } from '../config/DaemonConfig.js'
import type { SignalRClient } from '../signalr/SignalRClient.js'
import type { CustomTool, ToolContext } from '../turn/types.js'

import { buildCustomTools, ProposalSendError } from './CustomTools.js'
import type { ToolDescriptionResponse } from './fetchToolDescription.js'

// ============================================================================
// Test helpers
// ============================================================================

const API_BASE_URL = 'http://localhost:5338'
const RUNTIME_ID = '11111111-2222-3333-4444-555555555555'
const TOKEN =
  'eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiJ0ZXN0In0.aGVsbG8td29ybGQtc2lnbmF0dXJlLXNlZ21lbnQ'

const EXPECTED_URL = `${API_BASE_URL}/api/runtimes/${RUNTIME_ID}/proposals`

const STUB_PROPOSE_RUNTIME_SPEC: ToolDescriptionResponse = {
  description: 'Stubbed description from the backend.',
  inputSchema: {
    type: 'object',
    properties: {
      proposedSpec: { type: 'object' },
      reason: { type: 'string' },
    },
    required: ['proposedSpec', 'reason'],
  },
}

function makeLogger(): Logger {
  const log = {
    trace: vi.fn(),
    debug: vi.fn(),
    info: vi.fn(),
    warn: vi.fn(),
    error: vi.fn(),
    fatal: vi.fn(),
    child: vi.fn(),
  }
  log.child.mockImplementation(() => log)
  return log as unknown as Logger
}

/**
 * Minimal DaemonConfig stand-in — only the fields propose_runtime_spec touches.
 * `runtimeToken` is a getter so tests can swap the underlying value to exercise
 * the rotation path without the JWT-shape validation the real
 * `DaemonConfig.rotateToken` enforces.
 */
interface ConfigFake {
  mainApiUrl: URL
  runtimeId: string
  runtimeToken: string
}

function makeConfig(opts: {
  mainApiUrl?: string
  runtimeId?: string
  tokenGetter?: () => string
} = {}): ConfigFake {
  const url = new URL(opts.mainApiUrl ?? API_BASE_URL)
  const id = opts.runtimeId ?? RUNTIME_ID
  const tokenGetter = opts.tokenGetter ?? (() => TOKEN)
  const c: Partial<ConfigFake> = { mainApiUrl: url, runtimeId: id }
  Object.defineProperty(c, 'runtimeToken', {
    get: tokenGetter,
    enumerable: true,
  })
  return c as ConfigFake
}

function makeCtx(config: ConfigFake): ToolContext {
  return {
    signalr: {} as SignalRClient,
    config: config as unknown as DaemonConfig,
    sessionId: 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa',
    turnId: 'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb',
  }
}

function jsonResponse(status: number, body: unknown): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json' },
  })
}

function findProposeTool(
  fetchImpl: typeof fetch,
  config: ConfigFake,
  proposeRuntimeSpec: ToolDescriptionResponse = STUB_PROPOSE_RUNTIME_SPEC,
): CustomTool {
  const tools = buildCustomTools({
    config: config as unknown as DaemonConfig,
    logger: makeLogger(),
    fetchImpl,
    proposeRuntimeSpec,
  })
  const t = tools.find((x) => x.name === 'propose_runtime_spec')
  if (!t) throw new Error('propose_runtime_spec not found')
  return t
}

// ============================================================================
// Tests
// ============================================================================

describe('propose_runtime_spec — HTTP wiring', () => {
  it('happy path — POSTs to /api/runtimes/{id}/proposals and returns a model-safe success marker', async () => {
    // The model used to receive the backend's proposalId in the tool result
    // and would quote it back to the user in chat ("Resubmitted — new
    // proposal id <uuid>"). On at least one observed turn the LLM
    // hallucinated a different UUID entirely, telling the user a proposal
    // had been resubmitted that the daemon had never sent. We now strip the
    // UUID from the model-visible result; the id is logged for operator
    // debugging and pushed to the user out-of-band via the SignalR
    // confirmation card.
    const fetchImpl = vi.fn(async () =>
      jsonResponse(200, { proposalId: 'abc-123-def' }),
    ) as unknown as typeof fetch
    const config = makeConfig()
    const tool = findProposeTool(fetchImpl, config)

    const proposedSpec = {
      version: 3,
      services: [
        {
          kind: 'dotnet-mise',
          name: 'dotnet-api',
          values: { project: 'packages/dotnet-api', dotnetVersion: '9', port: 5338 },
        },
      ],
    }

    const result = (await tool.run(
      { proposedSpec, reason: 'project needs the .NET API to run' },
      makeCtx(config),
    )) as { ok: boolean; message?: string; proposalId?: string }

    expect(result.ok).toBe(true)
    expect(result.proposalId).toBeUndefined()
    expect(typeof result.message).toBe('string')
    expect(result.message).not.toContain('abc-123-def')

    expect((fetchImpl as unknown as ReturnType<typeof vi.fn>).mock.calls).toHaveLength(1)
    const [calledUrl, calledInit] = (fetchImpl as unknown as ReturnType<typeof vi.fn>)
      .mock.calls[0]! as [string, RequestInit]

    expect(calledUrl).toBe(EXPECTED_URL)
    expect(calledInit.method).toBe('POST')
    expect(calledInit.headers).toEqual({
      'Content-Type': 'application/json',
      Authorization: `Bearer ${TOKEN}`,
    })
    expect(typeof calledInit.body).toBe('string')
    const parsedBody = JSON.parse(calledInit.body as string) as Record<string, unknown>
    expect(parsedBody).toEqual({
      proposedSpec,
      reason: 'project needs the .NET API to run',
    })
  })

  it('400 with structured error → throws ProposalSendError with the backend code', async () => {
    const fetchImpl = vi.fn(async () =>
      jsonResponse(400, { error: 'unknown_preset_kind: foo' }),
    ) as unknown as typeof fetch
    const config = makeConfig()
    const tool = findProposeTool(fetchImpl, config)

    const promise = tool.run(
      {
        proposedSpec: {
          version: 3,
          services: [{ kind: 'foo', name: 'thing', values: {} }],
        },
        reason: 'pretend the backend rejected this proposal',
      },
      makeCtx(config),
    )

    await expect(promise).rejects.toBeInstanceOf(ProposalSendError)
    await expect(promise).rejects.toMatchObject({
      code: 'unknown_preset_kind: foo',
    })
  })

  it('400 with non-JSON body → throws ProposalSendError with status-coded fallback', async () => {
    const fetchImpl = vi.fn(
      async () => new Response('Bad Request', { status: 400 }),
    ) as unknown as typeof fetch
    const config = makeConfig()
    const tool = findProposeTool(fetchImpl, config)

    const promise = tool.run(
      {
        proposedSpec: { version: 3 },
        reason: 'this should be rejected with no JSON body',
      },
      makeCtx(config),
    )

    await expect(promise).rejects.toBeInstanceOf(ProposalSendError)
    await expect(promise).rejects.toMatchObject({ code: 'proposal_rejected_400' })
  })

  it('5xx → throws ProposalSendError(proposal_send_failed)', async () => {
    const fetchImpl = vi.fn(
      async () => new Response('Service Unavailable', { status: 503 }),
    ) as unknown as typeof fetch
    const config = makeConfig()
    const tool = findProposeTool(fetchImpl, config)

    const promise = tool.run(
      {
        proposedSpec: { version: 3 },
        reason: 'reason long enough to pass minLength validation',
      },
      makeCtx(config),
    )

    await expect(promise).rejects.toBeInstanceOf(ProposalSendError)
    await expect(promise).rejects.toMatchObject({ code: 'proposal_send_failed' })
  })

  it('network error → throws ProposalSendError(proposal_send_failed) with err.message', async () => {
    const fetchImpl = vi.fn(async () => {
      throw new Error('connection refused')
    }) as unknown as typeof fetch
    const config = makeConfig()
    const tool = findProposeTool(fetchImpl, config)

    const promise = tool.run(
      {
        proposedSpec: { version: 3 },
        reason: 'reason long enough to pass minLength validation',
      },
      makeCtx(config),
    )

    await expect(promise).rejects.toBeInstanceOf(ProposalSendError)
    await expect(promise).rejects.toMatchObject({
      code: 'proposal_send_failed',
      message: expect.stringContaining('connection refused') as unknown as string,
    })
  })

  it('reads runtimeToken at call time — token rotation lands on the next call', async () => {
    let current = 'token-A'
    const fetchImpl = vi.fn(async () =>
      jsonResponse(200, { proposalId: 'p1' }),
    ) as unknown as typeof fetch
    const config = makeConfig({ tokenGetter: () => current })
    const tool = findProposeTool(fetchImpl, config)

    await tool.run(
      {
        proposedSpec: { version: 3 },
        reason: 'first call uses token-A — long enough reason',
      },
      makeCtx(config),
    )

    current = 'token-B'

    await tool.run(
      {
        proposedSpec: { version: 3 },
        reason: 'second call should use token-B — long enough reason',
      },
      makeCtx(config),
    )

    const calls = (fetchImpl as unknown as ReturnType<typeof vi.fn>).mock.calls as [
      string,
      RequestInit,
    ][]
    expect(calls).toHaveLength(2)
    expect((calls[0]![1].headers as Record<string, string>).Authorization).toBe(
      'Bearer token-A',
    )
    expect((calls[1]![1].headers as Record<string, string>).Authorization).toBe(
      'Bearer token-B',
    )
  })

  it('trailing slash on mainApiUrl is normalised', async () => {
    const fetchImpl = vi.fn(async () =>
      jsonResponse(200, { proposalId: 'p1' }),
    ) as unknown as typeof fetch
    const config = makeConfig({ mainApiUrl: `${API_BASE_URL}/` })
    const tool = findProposeTool(fetchImpl, config)

    await tool.run(
      {
        proposedSpec: { version: 3 },
        reason: 'a reason long enough to pass minLength check',
      },
      makeCtx(config),
    )

    const [calledUrl] = (fetchImpl as unknown as ReturnType<typeof vi.fn>)
      .mock.calls[0]! as [string, RequestInit]
    expect(calledUrl).toBe(EXPECTED_URL)
  })

  it('missing proposedSpec → rejected locally without hitting fetch', async () => {
    const fetchImpl = vi.fn(async () =>
      jsonResponse(200, { proposalId: 'p1' }),
    ) as unknown as typeof fetch
    const config = makeConfig()
    const tool = findProposeTool(fetchImpl, config)

    const result = (await tool.run(
      { reason: 'no spec' },
      makeCtx(config),
    )) as { ok: boolean; error?: string }

    expect(result.ok).toBe(false)
    expect(result.error).toMatch(/invalid input/)
    expect((fetchImpl as unknown as ReturnType<typeof vi.fn>).mock.calls).toHaveLength(0)
  })

  it('missing reason → rejected locally without hitting fetch', async () => {
    const fetchImpl = vi.fn(async () =>
      jsonResponse(200, { proposalId: 'p1' }),
    ) as unknown as typeof fetch
    const config = makeConfig()
    const tool = findProposeTool(fetchImpl, config)

    const result = (await tool.run(
      { proposedSpec: { version: 3 } },
      makeCtx(config),
    )) as { ok: boolean; error?: string }

    expect(result.ok).toBe(false)
    expect(result.error).toMatch(/invalid input/)
    expect((fetchImpl as unknown as ReturnType<typeof vi.fn>).mock.calls).toHaveLength(0)
  })

  it('description + inputSchema flow through from deps verbatim', async () => {
    const fetchImpl = vi.fn(async () =>
      jsonResponse(200, { proposalId: 'p1' }),
    ) as unknown as typeof fetch
    const config = makeConfig()
    const description = 'CUSTOM description sentinel'
    const inputSchema = {
      type: 'object',
      properties: {
        proposedSpec: { type: 'object' },
        reason: { type: 'string' },
      },
      required: ['proposedSpec', 'reason'],
      'x-sentinel': 'preset-flow-through',
    }
    const tool = findProposeTool(fetchImpl, config, { description, inputSchema })

    expect(tool.description).toBe(description)
    expect(tool.inputSchema).toEqual(inputSchema)
  })

  it('forwards an arbitrary proposedSpec shape verbatim (daemon does no structural validation)', async () => {
    // The whole point of V3 is the daemon doesn't enforce structure — that
    // lives backend-side in PresetExpander. We assert the body is forwarded
    // exactly so a future preset (e.g. one with a brand-new `values` key)
    // requires no daemon change.
    const fetchImpl = vi.fn(async () =>
      jsonResponse(200, { proposalId: 'p1' }),
    ) as unknown as typeof fetch
    const config = makeConfig()
    const tool = findProposeTool(fetchImpl, config)

    const exoticSpec = {
      version: 3,
      services: [
        {
          kind: 'future-preset-not-yet-known-to-the-daemon',
          name: 'future-service',
          values: {
            stringParam: 'hello',
            numberParam: 42,
            booleanParam: true,
            nested: { deep: 'value' },
          },
        },
      ],
      install: '# whatever the preset author wrote',
      setup: '# whatever the preset author wrote',
    }

    await tool.run(
      { proposedSpec: exoticSpec, reason: 'forward unknown shape verbatim' },
      makeCtx(config),
    )

    const [, calledInit] = (fetchImpl as unknown as ReturnType<typeof vi.fn>)
      .mock.calls[0]! as [string, RequestInit]
    const parsed = JSON.parse(calledInit.body as string) as Record<string, unknown>
    expect(parsed.proposedSpec).toEqual(exoticSpec)
  })
})

// Fetcher for the `propose_runtime_spec` tool description + JSON schema.
//
// Why this exists
// ---------------
// Pre-V3 the daemon hard-coded a ~200-line prose description and a JSON Schema
// that enumerated every legal service shape. Adding a new preset (or even
// fixing a typo in the worked example) meant cutting a daemon release.
//
// V3 moves the source of truth into the backend's `ServicePresets` registry.
// At startup the daemon GETs `/api/runtime-presets/tool-description`, which
// is `[AllowAnonymous]` for exactly this reason — bootstrap runs before any
// user auth context exists and we need the tool registered before the first
// turn ever fires.
//
// The endpoint returns:
//   { description: string, inputSchema: object }
// where `inputSchema` is a `oneOf` discriminated by `kind` over the live
// preset slugs. The daemon doesn't introspect either field — it forwards them
// verbatim to the Cursor SDK MCP client and POSTs whatever args the model produces
// straight to the backend's `CreateRuntimeProposal` endpoint.
//
// Fallback
// --------
// If the fetch fails (backend briefly down, transient DNS hiccup, etc.) we
// fall back to a minimal description + an "any object" schema. The daemon
// still boots, the tool is still registered, and the backend rejects bad
// proposals with a clear error. This is preferable to wedging the runtime on
// a startup-time backend dependency.

import type { Logger } from 'pino'

export interface ToolDescriptionResponse {
  description: string
  inputSchema: Record<string, unknown>
}

const FALLBACK: ToolDescriptionResponse = {
  description:
    'Propose a runtime spec for this project. The full preset list could ' +
    'not be loaded at daemon startup — try again later or contact an ' +
    'operator. Shape: { proposedSpec: { version: 3, services: [{ kind, ' +
    'name, values }], install?, setup? }, reason }.',
  inputSchema: {
    type: 'object',
    properties: {
      proposedSpec: { type: 'object' },
      reason: { type: 'string' },
    },
    required: ['proposedSpec', 'reason'],
  },
}

/**
 * Fetch the `propose_runtime_spec` tool description + JSON schema from the
 * backend. Returns a sane fallback on any error so daemon boot is never
 * blocked on backend availability.
 */
export async function fetchToolDescription(
  apiBaseUrl: string,
  logger: Logger,
  fetchImpl: typeof fetch = fetch,
): Promise<ToolDescriptionResponse> {
  const base = apiBaseUrl.endsWith('/') ? apiBaseUrl.slice(0, -1) : apiBaseUrl
  const url = `${base}/api/runtime-presets/tool-description`
  logger.info({ url }, 'fetching propose_runtime_spec tool description')
  try {
    const res = await fetchImpl(url)
    if (!res.ok) {
      throw new Error(
        `tool-description fetch failed: ${res.status} ${res.statusText}`,
      )
    }
    const body = (await res.json()) as Partial<ToolDescriptionResponse>
    if (
      typeof body.description !== 'string' ||
      body.inputSchema === null ||
      typeof body.inputSchema !== 'object'
    ) {
      throw new Error('tool-description response missing required fields')
    }
    logger.info(
      { descriptionBytes: body.description.length },
      'propose_runtime_spec tool description loaded',
    )
    return {
      description: body.description,
      inputSchema: body.inputSchema as Record<string, unknown>,
    }
  } catch (err) {
    logger.error(
      { err },
      'failed to fetch propose_runtime_spec tool description; using fallback',
    )
    return FALLBACK
  }
}

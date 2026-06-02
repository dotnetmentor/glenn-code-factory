// Platform-defined custom tools exposed via the daemon-tools MCP server.
//
// Three tools:
//   - `propose_runtime_spec` — POST a RuntimeSpecV3 proposal to main API.
//     Description + JSON schema are fetched from the backend at daemon
//     startup (see `fetchToolDescription.ts`) because the source of truth
//     is the live `ServicePresets` registry — adding a preset in super
//     admin should make the agent know about it without a daemon release.
//     The daemon does not validate the spec shape; it forwards the args
//     straight to `CreateRuntimeProposal` and lets the backend's
//     `PresetExpander` do all the structural work.
//   - `restart_service` — restart a supervisord-managed service (with approval).
//   - `dry_run_install` — execute a bash snippet in the same shell environment
//     the bootstrap InstallStage uses, so the agent can verify a proposed
//     `install` snippet actually works *before* calling propose_runtime_spec.

import { execFile } from 'node:child_process'
import { promisify } from 'node:util'
import Ajv from 'ajv'
import type { Logger } from 'pino'

import type { DaemonConfig } from '../config/DaemonConfig.js'
import type { BootIssue } from '../bootstrap/BootIssueStore.js'
import { ChildProcessExecutor } from '../runtime/ChildProcessExecutor.js'
import type { IExecutor } from '../runtime/IExecutor.js'
import { bootstrapEnv } from '../runtime/BootstrapEnvironment.js'
import type { CustomTool, ToolContext, ToolResult } from '../turn/types.js'

import type { ToolDescriptionResponse } from './fetchToolDescription.js'

const execFileAsync = promisify(execFile)

const ajv = new Ajv({ allErrors: true })

// ============================================================================
// Tool 1 — propose_runtime_spec
// ============================================================================

export class ProposalSendError extends Error {
  constructor(
    public readonly code: string,
    message: string,
  ) {
    super(message)
    this.name = 'ProposalSendError'
  }
}

/**
 * V3 args shape — the daemon only knows the outer envelope. The `proposedSpec`
 * is forwarded verbatim; the backend owns the structural contract via its
 * `RuntimeSpecV3` record + `PresetExpander`. We deliberately type it as
 * `unknown` so a future preset shape doesn't require a daemon change.
 */
interface ProposeRuntimeSpecArgs {
  proposedSpec: unknown
  reason: string
}

function buildProposeRuntimeSpec(deps: {
  config: DaemonConfig
  logger: Logger
  fetchImpl: typeof fetch
  description: string
  inputSchema: object
}): CustomTool {
  return {
    name: 'propose_runtime_spec',
    description: deps.description,
    inputSchema: deps.inputSchema,
    async run(args: unknown, _ctx: ToolContext): Promise<ToolResult> {
      if (args === null || typeof args !== 'object') {
        return { ok: false, error: 'invalid input: args must be an object' }
      }
      const proposal = args as Partial<ProposeRuntimeSpecArgs>
      if (proposal.proposedSpec === undefined) {
        return { ok: false, error: 'invalid input: proposedSpec is required' }
      }
      if (typeof proposal.reason !== 'string' || proposal.reason.length === 0) {
        return { ok: false, error: 'invalid input: reason is required' }
      }

      const url = `${stripTrailingSlash(deps.config.mainApiUrl.toString())}/api/runtimes/${deps.config.runtimeId}/proposals`

      const body = {
        proposedSpec: proposal.proposedSpec,
        reason: proposal.reason,
      }

      let response: Response
      try {
        response = await deps.fetchImpl(url, {
          method: 'POST',
          headers: {
            'Content-Type': 'application/json',
            Authorization: `Bearer ${deps.config.runtimeToken}`,
          },
          body: JSON.stringify(body),
        })
      } catch (err) {
        const msg = err instanceof Error ? err.message : String(err)
        deps.logger.error({ err }, 'propose_runtime_spec network error')
        throw new ProposalSendError('proposal_send_failed', msg)
      }

      if (response.ok) {
        const parsed = (await response.json()) as { proposalId?: string }
        if (typeof parsed.proposalId === 'string' && parsed.proposalId.length > 0) {
          deps.logger.info(
            { proposalId: parsed.proposalId },
            'propose_runtime_spec accepted by backend',
          )
        } else {
          deps.logger.warn(
            'propose_runtime_spec: backend returned 2xx without a proposalId field',
          )
        }
        return {
          ok: true,
          message:
            'Proposal submitted. The user will see a confirmation card in the UI and decide whether to approve. Do NOT quote any proposal id — none is exposed to you.',
        }
      }

      if (response.status >= 400 && response.status < 500) {
        let errorCode: string | undefined
        try {
          const errBody = (await response.json()) as { error?: string }
          if (typeof errBody.error === 'string') errorCode = errBody.error
        } catch {
          // Body wasn't JSON — fall back to status-coded error.
        }
        const code = errorCode ?? `proposal_rejected_${response.status}`
        deps.logger.error(
          { status: response.status, code },
          'propose_runtime_spec rejected by backend',
        )
        throw new ProposalSendError(
          code,
          `Backend rejected proposal: ${errorCode ?? response.statusText}`,
        )
      }

      deps.logger.error(
        { status: response.status },
        'propose_runtime_spec backend 5xx',
      )
      throw new ProposalSendError(
        'proposal_send_failed',
        `Backend ${response.status}: ${response.statusText}`,
      )
    },
  }
}

function stripTrailingSlash(s: string): string {
  return s.endsWith('/') ? s.slice(0, -1) : s
}

// ============================================================================
// Tool 2 — restart_service
// ============================================================================

const VALID_SERVICE_NAME = /^[a-zA-Z0-9_-]{1,64}$/

const restartServiceSchema = {
  $schema: 'http://json-schema.org/draft-07/schema#',
  type: 'object',
  properties: {
    name: { type: 'string', pattern: '^[a-zA-Z0-9_-]{1,64}$' },
    reason: { type: 'string', minLength: 10 },
  },
  required: ['name', 'reason'],
  additionalProperties: false,
} as const

interface RestartServiceArgs {
  name: string
  reason: string
}

function buildRestartService(deps: {
  config: DaemonConfig
  logger: Logger
  exec?: typeof execFileAsync
  approveRestart?: (args: {
    name: string
    reason: string
    sessionId: string
    turnId: string
  }) => Promise<{ approved: boolean; reason?: string }>
}): CustomTool {
  const exec = deps.exec ?? execFileAsync
  return {
    name: 'restart_service',
    description:
      'Restart a supervisord-managed service inside this runtime. Use sparingly — only when ' +
      'a service has clearly hung. The restart goes through main API for permission first.',
    inputSchema: restartServiceSchema,
    async run(args: unknown, ctx: ToolContext): Promise<ToolResult> {
      const validation = validateAgainstSchema(restartServiceSchema, args)
      if (!validation.ok) {
        return { ok: false, error: `invalid input: ${validation.errors}` }
      }
      const input = args as RestartServiceArgs

      if (!VALID_SERVICE_NAME.test(input.name)) {
        return { ok: false, error: 'invalid service name' }
      }

      const approval = deps.approveRestart
        ? await deps.approveRestart({
            name: input.name,
            reason: input.reason,
            sessionId: ctx.sessionId,
            turnId: ctx.turnId,
          })
        : { approved: false, reason: 'not_implemented' }

      if (!approval.approved) {
        return {
          ok: false,
          approved: false,
          reason: approval.reason ?? 'not_approved',
        }
      }

      try {
        const { stdout, stderr } = await exec('supervisorctl', ['restart', input.name])
        return {
          ok: true,
          stdout: stdout.trim(),
          stderr: stderr.trim(),
        }
      } catch (err) {
        deps.logger.error({ err, name: input.name }, 'supervisorctl failed')
        return {
          ok: false,
          error: 'supervisorctl_failed',
          message: err instanceof Error ? err.message : String(err),
        }
      }
    },
  }
}

// ============================================================================
// Tool 3 — dry_run_install
// ============================================================================
//
// Why this tool exists
// --------------------
// Before this tool, an agent could propose an install snippet that "worked"
// when tested via Cursor's shell tool (which runs as a child of supervisord
// with `PATH=/data/mise/shims:/usr/local/sbin:...` exported by entrypoint.sh)
// and then have it fail at bootstrap time because InstallStage builds its own
// `env` block from scratch — same PATH conceptually, but assembled via a
// different code path and writing the script to `/tmp/install-*.sh` first.
// The agent had no way to verify that "works in my shell" implied "works in
// the install stage." It guessed.
//
// `dry_run_install` closes the loop by executing the snippet through
// `IExecutor.run('bash', ['-c', <heredoc>], { env, cwd })` with EXACTLY the
// same env/cwd/heredoc shape `InstallStage.ts` uses, sourced from the shared
// `BootstrapEnvironment` module so they cannot drift.
//
// What it does NOT do
// -------------------
//   - It does NOT touch the install-hash store at `/data/.glenn/
//     install-hashes.json`. Repeated dry runs are free; they never trick the
//     daemon into skipping a real install.
//   - It does NOT need approval. The agent already has unrestricted shell
//     access via Cursor's `shell` tool; this tool gives the same capability
//     in a more bootstrap-faithful environment, not a stronger capability.
//   - It does NOT enforce idempotency or `set -euo pipefail`. Whatever script
//     the agent passes is what runs — including footguns. That's by design:
//     a dry run that diverges from "what install would actually do" is
//     useless.

const DRY_RUN_INSTALL_TIMEOUT_MS = 5 * 60_000
const DRY_RUN_TAIL_BYTES = 8 * 1024 // ~30 lines of typical install output

const dryRunInstallSchema = {
  $schema: 'http://json-schema.org/draft-07/schema#',
  type: 'object',
  properties: {
    script: { type: 'string', minLength: 1, maxLength: 64 * 1024 },
    timeoutMs: {
      type: 'integer',
      minimum: 1_000,
      maximum: DRY_RUN_INSTALL_TIMEOUT_MS,
    },
  },
  required: ['script'],
  additionalProperties: false,
} as const

interface DryRunInstallArgs {
  script: string
  timeoutMs?: number
}

function tailUtf8(s: string, maxBytes: number): string {
  if (Buffer.byteLength(s, 'utf8') <= maxBytes) return s
  // Slice from the END so the tool surfaces what failed, not a banner.
  const buf = Buffer.from(s, 'utf8')
  return buf.slice(buf.length - maxBytes).toString('utf8')
}

function buildDryRunInstall(deps: {
  logger: Logger
  executor: IExecutor
  now?: () => number
}): CustomTool {
  const now = deps.now ?? (() => Date.now())
  return {
    name: 'dry_run_install',
    description:
      'Execute a bash snippet in the exact shell environment that the ' +
      'bootstrap InstallStage uses (cwd `/`, ' +
      'PATH=/data/mise/shims:/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin, ' +
      'HOME=/home/agent, `bash -c` heredoc shape). Use this BEFORE ' +
      '`propose_runtime_spec` to verify the snippet actually works in the ' +
      'boot-time environment — that is NOT the same as your interactive ' +
      "shell's environment, and the difference is the #1 source of broken " +
      'specs that wedge runtimes.\n' +
      '\n' +
      'Returns `{ exitCode, durationMs, stdoutTail, stderrTail, timedOut }`. ' +
      'Exit 0 = the snippet is safe to put in `install`. Anything else = ' +
      'fix it before proposing. `stdoutTail` / `stderrTail` are capped at ' +
      '8 KB each from the END of the stream (where failure messages live).\n' +
      '\n' +
      'This tool does NOT mutate the install-hash cache. Repeated dry runs ' +
      'are free; they never make the real bootstrap skip an install.\n' +
      '\n' +
      'Args: `script` (required, 1–64 KB bash), `timeoutMs` (optional, ' +
      '1000–300000, default 300000).',
    inputSchema: dryRunInstallSchema,
    async run(args: unknown, _ctx: ToolContext): Promise<ToolResult> {
      const validation = validateAgainstSchema(dryRunInstallSchema, args)
      if (!validation.ok) {
        return { ok: false, error: `invalid input: ${validation.errors}` }
      }
      const input = args as DryRunInstallArgs
      const timeoutMs = input.timeoutMs ?? DRY_RUN_INSTALL_TIMEOUT_MS

      // Use the same heredoc-into-tempfile shape InstallStage does so multi-
      // line scripts, single quotes, and bash builtins all behave identically.
      // The script body is interpolated unquoted into the heredoc; the sentinel
      // is fixed and a literal `__GLENN_DRY_RUN_EOF__` in the user's script
      // would collide. We accept that as a documented edge case — the install
      // stage has the same limitation with `__GLENN_INSTALL_EOF__`.
      const scriptPath = `/tmp/dry-run-install-${now()}-${process.pid}.sh`
      const heredoc =
        `cat > "${scriptPath}" <<'__GLENN_DRY_RUN_EOF__'\n` +
        `${input.script}\n` +
        `__GLENN_DRY_RUN_EOF__\n` +
        `bash "${scriptPath}"\n` +
        // Always clean the tempfile so /tmp doesn't accumulate across dry runs.
        `rm -f "${scriptPath}"\n`

      const startedAt = now()
      let stdout = ''
      let stderr = ''
      let exitCode = 0
      let timedOut = false

      try {
        const result = await deps.executor.run(
          'bash',
          ['-c', heredoc],
          {
            cwd: '/',
            env: bootstrapEnv(),
            timeoutMs,
            allowNonZero: true,
            onStdout: (chunk) => {
              stdout += chunk
            },
            onStderr: (chunk) => {
              stderr += chunk
            },
          },
        )
        exitCode = result.exitCode
        // Streaming captures every chunk; the result object also carries the
        // final captures, but onStdout/onStderr are the source of truth for
        // partial output on signal-kill.
      } catch (err) {
        // IExecutor throws on signal-kill (timeout). Anything else surfaces
        // as a thrown Error.
        const message = err instanceof Error ? err.message : String(err)
        if (message.includes('killed by signal')) {
          timedOut = true
          exitCode = -1
        } else {
          deps.logger.warn({ err }, 'dry_run_install executor threw')
          return {
            ok: false,
            error: 'executor_failed',
            message,
          }
        }
      }

      const durationMs = now() - startedAt
      const stdoutTail = tailUtf8(stdout, DRY_RUN_TAIL_BYTES)
      const stderrTail = tailUtf8(stderr, DRY_RUN_TAIL_BYTES)

      deps.logger.info(
        {
          exitCode,
          durationMs,
          timedOut,
          scriptBytes: input.script.length,
          stdoutBytes: stdout.length,
          stderrBytes: stderr.length,
        },
        'dry_run_install completed',
      )

      return {
        ok: true,
        exitCode,
        durationMs,
        stdoutTail,
        stderrTail,
        timedOut,
      }
    },
  }
}

// ============================================================================
// Self-heal loop shared prose
// ============================================================================
//
// All three self-heal tools document the SAME primary loop so the agent — woken
// by a "Let agent fix it" repair turn — always knows the canonical path even if
// it only reads one tool's description. Keep this in one constant so the three
// descriptions can't drift.

const SELF_HEAL_LOOP =
  'PRIMARY SELF-HEAL LOOP (use in this order):\n' +
  '  1. `get_boot_issues` — see exactly which spec stages/services failed this boot.\n' +
  '  2. `get_runtime_spec` — read the CURRENT applied spec that produced those issues.\n' +
  '  3. `dry_run_install` — validate your corrected `install` snippet in the real boot env.\n' +
  '  4. `propose_runtime_spec` — submit the fix. During a repair turn this AUTO-APPLIES ' +
  'LIVE (delta-apply, NO reboot); SpecHealth flips Degraded→Healthy and the banner clears.\n' +
  'Only fall back to `request_rebootstrap` when the rootfs is genuinely wedged and a live ' +
  'delta-apply cannot recover it.'

// ============================================================================
// Tool 4 — get_runtime_spec  (read-only, no approval)
// ============================================================================
//
// Surfaces the CURRENT applied RuntimeSpec the daemon booted with — sourced from
// the in-memory `BootstrapState` the orchestrator populated in FetchingStage
// (`bootstrapState.payload.runtimeSpec` + the payload envelope `version`). Before
// this tool the agent had NO way to see the spec that failed, so this is the
// critical diagnostic input for the self-heal loop. Read-only: it never mutates
// anything and needs no approval.

const getRuntimeSpecSchema = {
  $schema: 'http://json-schema.org/draft-07/schema#',
  type: 'object',
  properties: {},
  additionalProperties: false,
} as const

function buildGetRuntimeSpec(deps: {
  logger: Logger
  /**
   * Snapshot the current applied spec from the daemon's in-memory bootstrap
   * carrier. Returns `null` when bootstrap hasn't fetched a payload yet (the
   * spec is genuinely unknown), so the tool can report that distinctly rather
   * than throwing the BootstrapState getter's "read before populated" error.
   */
  getRuntimeSpec: () => { version: string; runtimeSpec: unknown } | null
}): CustomTool {
  return {
    name: 'get_runtime_spec',
    description:
      'Return the CURRENT applied RuntimeSpec this runtime booted with (the spec ' +
      'JSON plus its envelope `version`). Read-only — no approval, no side effects. ' +
      'This is the spec that produced any boot issues, so read it FIRST when ' +
      'diagnosing a degraded boot: it is the only way to see what actually ran.\n' +
      '\n' +
      'Returns `{ ok, available, version, spec }`. `available:false` means bootstrap ' +
      "has not fetched a spec yet (nothing to diagnose). The `spec` is the runtime's " +
      'RuntimeSpecV2 (install bash, services[], setup bash).\n' +
      '\n' +
      SELF_HEAL_LOOP,
    inputSchema: getRuntimeSpecSchema,
    async run(_args: unknown, _ctx: ToolContext): Promise<ToolResult> {
      let snapshot: { version: string; runtimeSpec: unknown } | null
      try {
        snapshot = deps.getRuntimeSpec()
      } catch (err) {
        deps.logger.warn({ err }, 'get_runtime_spec: failed to read bootstrap state')
        snapshot = null
      }
      if (snapshot === null) {
        return {
          ok: true,
          available: false,
          message:
            'No runtime spec is available yet — bootstrap has not fetched a ' +
            'BootstrapPayload. There is nothing to diagnose.',
        }
      }
      return {
        ok: true,
        available: true,
        version: snapshot.version,
        spec: snapshot.runtimeSpec,
      }
    },
  }
}

// ============================================================================
// Tool 5 — get_boot_issues  (read-only, no approval)
// ============================================================================
//
// Returns the BootIssue[] collected this boot from the shared in-memory
// `BootIssueStore` (the SAME instance the orchestrator wrote degraded-spec
// issues into during a non-critical-stage failure). Read-only, no approval.

const getBootIssuesSchema = {
  $schema: 'http://json-schema.org/draft-07/schema#',
  type: 'object',
  properties: {},
  additionalProperties: false,
} as const

function buildGetBootIssues(deps: {
  logger: Logger
  /** Defensive snapshot of the shared BootIssueStore (record-order). */
  listBootIssues: () => BootIssue[]
}): CustomTool {
  return {
    name: 'get_boot_issues',
    description:
      'Return the list of non-fatal boot issues collected during THIS boot ' +
      '(degraded-online bootstrap). Each issue is `{ stage, service?, reason, ' +
      'detail?, occurredAt }` — the failing spec stage (install / running-setup / ' +
      'starting-services), the service it concerns (when service-scoped), a one-line ' +
      'reason, and an optional log/stderr tail. Read-only — no approval.\n' +
      '\n' +
      'Returns `{ ok, count, issues }`. `count:0` means the spec applied cleanly ' +
      '(healthy boot, nothing to fix). Start here when a repair turn wakes you.\n' +
      '\n' +
      SELF_HEAL_LOOP,
    inputSchema: getBootIssuesSchema,
    async run(_args: unknown, _ctx: ToolContext): Promise<ToolResult> {
      let issues: BootIssue[]
      try {
        issues = deps.listBootIssues()
      } catch (err) {
        deps.logger.warn({ err }, 'get_boot_issues: failed to read boot-issue store')
        issues = []
      }
      return {
        ok: true,
        count: issues.length,
        issues,
      }
    },
  }
}

// ============================================================================
// Tool 6 — get_preview_url  (read-only, no approval)
// ============================================================================
//
// Returns the public HTTPS URL the user sees in the Preview tab. Sourced from
// PREVIEW_HOSTNAME (and PREVIEW_PORT for local target context) stamped on the
// Fly machine by RuntimeProvisionerJob when a tunnel is allocated.

const getPreviewUrlSchema = {
  $schema: 'http://json-schema.org/draft-07/schema#',
  type: 'object',
  properties: {},
  additionalProperties: false,
} as const

export type PreviewEnvSnapshot = {
  hostname?: string
  previewPort?: string
}

export function readPreviewEnvFromProcess(): PreviewEnvSnapshot {
  const rawHostname = process.env['PREVIEW_HOSTNAME']?.trim()
  const rawPort = process.env['PREVIEW_PORT']?.trim()
  return {
    ...(rawHostname !== undefined && rawHostname !== '' ? { hostname: rawHostname } : {}),
    ...(rawPort !== undefined && rawPort !== '' ? { previewPort: rawPort } : {}),
  }
}

function buildGetPreviewUrl(deps: {
  logger: Logger
  getPreviewEnv?: () => PreviewEnvSnapshot
}): CustomTool {
  const getPreviewEnv = deps.getPreviewEnv ?? readPreviewEnvFromProcess
  return {
    name: 'get_preview_url',
    description:
      'Return the public HTTPS preview URL for this runtime (the same link the user ' +
      'opens in the Preview tab). Read-only — no approval, no side effects.\n' +
      '\n' +
      'Returns `{ ok, available, previewUrl?, hostname?, previewPort? }`. ' +
      '`available:false` when no Cloudflare preview tunnel is allocated for this ' +
      'branch/runtime. The URL does not guarantee the app is up — only that a tunnel ' +
      'hostname was provisioned.',
    inputSchema: getPreviewUrlSchema,
    async run(_args: unknown, _ctx: ToolContext): Promise<ToolResult> {
      let env: PreviewEnvSnapshot
      try {
        env = getPreviewEnv()
      } catch (err) {
        deps.logger.warn({ err }, 'get_preview_url: failed to read preview env')
        env = {}
      }
      const hostname = env.hostname?.trim()
      if (hostname === undefined || hostname === '') {
        return {
          ok: true,
          available: false,
          message:
            'No preview tunnel is allocated for this runtime (PREVIEW_HOSTNAME is unset).',
        }
      }
      const previewPort = env.previewPort?.trim() || '5173'
      return {
        ok: true,
        available: true,
        previewUrl: `https://${hostname}`,
        hostname,
        previewPort,
      }
    },
  }
}

// ============================================================================
// Tool 7 — request_rebootstrap  (ESCAPE HATCH — last resort)
// ============================================================================
//
// Triggers a full re-bootstrap via the EXISTING force-rebootstrap path (the same
// teardown the server-initiated `ForceRebootstrap` push runs: abort in-flight
// bootstrap → clean shutdown → exit so supervisord respawns the daemon and
// FetchingStage pulls a fresh BootstrapPayload). We do NOT invent a new
// mechanism — the composition root hands us a `triggerRebootstrap` callback that
// is the very same function wired to the SignalR handler.
//
// No separate approval (the "Let agent fix it" click already granted consent),
// BUT a `reason` (min 10 chars) is required so the audit trail explains why the
// rootfs needed a hard reboot rather than a live delta-apply.

const REBOOTSTRAP_MIN_REASON = 10

const requestRebootstrapSchema = {
  $schema: 'http://json-schema.org/draft-07/schema#',
  type: 'object',
  properties: {
    reason: { type: 'string', minLength: REBOOTSTRAP_MIN_REASON },
  },
  required: ['reason'],
  additionalProperties: false,
} as const

interface RequestRebootstrapArgs {
  reason: string
}

function buildRequestRebootstrap(deps: {
  logger: Logger
  /**
   * Reuse of the EXISTING force-rebootstrap teardown. The composition root
   * passes the same callback it wires to `signalr.onForceRebootstrap` — abort
   * in-flight bootstrap, run the shutdown coordinator, exit the process. This
   * resolves and then the process tears down; the agent turn ends as a result.
   */
  triggerRebootstrap: (reason: string) => void | Promise<void>
}): CustomTool {
  return {
    name: 'request_rebootstrap',
    description:
      'ESCAPE HATCH — LAST RESORT. Trigger a FULL re-bootstrap of this runtime. ' +
      '⚠️ This RESTARTS THE RUNTIME and ENDS THE CURRENT AGENT TURN immediately: ' +
      'the daemon aborts any in-flight bootstrap, shuts down, and exits so it is ' +
      'respawned and re-pulls a fresh spec. Anything you have not already applied ' +
      'is lost.\n' +
      '\n' +
      'DO NOT reach for this first. The normal repair path applies a corrected spec ' +
      'LIVE with no reboot. Only call `request_rebootstrap` AFTER a fixed spec has ' +
      'been applied and a clean reboot is genuinely needed — e.g. the rootfs is ' +
      'wedged and a live delta-apply cannot recover it.\n' +
      '\n' +
      'Requires `reason` (string, min 10 chars) explaining why a hard reboot — not a ' +
      'live apply — is necessary. No separate approval is requested (consent was ' +
      'already given when the repair was started).\n' +
      '\n' +
      SELF_HEAL_LOOP,
    inputSchema: requestRebootstrapSchema,
    async run(args: unknown, _ctx: ToolContext): Promise<ToolResult> {
      const validation = validateAgainstSchema(requestRebootstrapSchema, args)
      if (!validation.ok) {
        return { ok: false, error: `invalid input: ${validation.errors}` }
      }
      const input = args as RequestRebootstrapArgs
      // Defensive trim check — the schema enforces raw length, but a reason of
      // pure whitespace is useless in the audit trail.
      if (input.reason.trim().length < REBOOTSTRAP_MIN_REASON) {
        return {
          ok: false,
          error: `invalid input: reason must be at least ${REBOOTSTRAP_MIN_REASON} characters`,
        }
      }

      deps.logger.warn(
        { reason: input.reason },
        'request_rebootstrap: agent requested full re-bootstrap (escape hatch)',
      )

      // Fire the existing force-rebootstrap teardown. We intentionally do NOT
      // await to completion before returning the tool result: the teardown
      // exits the process, so the agent turn ends regardless. Kick it off and
      // return an acknowledgement so the SDK has a result to surface.
      void Promise.resolve()
        .then(() => deps.triggerRebootstrap(input.reason))
        .catch((err) => {
          deps.logger.error({ err }, 'request_rebootstrap: triggerRebootstrap threw')
        })

      return {
        ok: true,
        message:
          'Re-bootstrap initiated. The runtime is restarting and this turn is ending now. ' +
          'It will come back up applying a freshly fetched spec.',
      }
    },
  }
}

// ============================================================================
// Factory
// ============================================================================

export interface BuildCustomToolsDeps {
  config: DaemonConfig
  logger: Logger
  exec?: typeof execFileAsync
  approveRestart?: (args: {
    name: string
    reason: string
    sessionId: string
    turnId: string
  }) => Promise<{ approved: boolean; reason?: string }>
  fetchImpl?: typeof fetch
  /**
   * Executor used by `dry_run_install` to spawn the candidate bash snippet.
   * Defaults to a fresh `ChildProcessExecutor` — tests inject a fake so they
   * never spawn a real `bash`. Sharing the executor with the bootstrap stages
   * is fine but not required; the only contract is `IExecutor.run`.
   */
  executor?: IExecutor
  /**
   * Description + JSON schema for `propose_runtime_spec`. Fetched once at
   * daemon startup from `/api/runtime-presets/tool-description` (see
   * `fetchToolDescription.ts`). The daemon never composes either field on
   * its own — the backend owns the prose and the preset enumeration so that
   * a new preset in super admin is immediately reachable by the agent
   * without a daemon release.
   */
  proposeRuntimeSpec: ToolDescriptionResponse
  // === Self-heal tools (self-healing-runtime-specs, card D2) ===
  // The three callbacks below bind the read-only diagnostic tools and the
  // rebootstrap escape hatch to live daemon state, threaded from the
  // composition root. All are OPTIONAL so minimal/legacy callers (and the
  // existing tests) keep working — the tools are always registered but
  // degrade to a safe "unavailable" result when their dependency is absent.
  /**
   * Snapshot the CURRENT applied spec from the daemon's in-memory
   * `BootstrapState` carrier (`{ version, runtimeSpec }`), or `null` when no
   * payload has been fetched yet. Read by `get_runtime_spec`.
   */
  getRuntimeSpec?: () => { version: string; runtimeSpec: unknown } | null
  /**
   * Defensive snapshot of the shared `BootIssueStore` this boot. The SAME
   * instance the orchestrator records degraded-spec issues into. Read by
   * `get_boot_issues`.
   */
  listBootIssues?: () => BootIssue[]
  /**
   * Snapshot `PREVIEW_HOSTNAME` / `PREVIEW_PORT` for `get_preview_url`. Defaults
   * to `readPreviewEnvFromProcess()`; tests inject a stub.
   */
  getPreviewEnv?: () => PreviewEnvSnapshot
  /**
   * Reuse of the EXISTING force-rebootstrap teardown (same callback the
   * composition root wires to `signalr.onForceRebootstrap`). Invoked by
   * `request_rebootstrap`.
   */
  triggerRebootstrap?: (reason: string) => void | Promise<void>
}

export function buildCustomTools(deps: BuildCustomToolsDeps): CustomTool[] {
  const childLogger = deps.logger.child({ module: 'custom-tools' })
  const fetchImpl: typeof fetch =
    deps.fetchImpl ?? ((...args) => fetch(...args))
  const proposeOpts: {
    config: DaemonConfig
    logger: Logger
    fetchImpl: typeof fetch
    description: string
    inputSchema: object
  } = {
    config: deps.config,
    logger: childLogger,
    fetchImpl,
    description: deps.proposeRuntimeSpec.description,
    inputSchema: deps.proposeRuntimeSpec.inputSchema,
  }
  const restartOpts: {
    config: DaemonConfig
    logger: Logger
    exec?: typeof execFileAsync
    approveRestart?: (args: {
      name: string
      reason: string
      sessionId: string
      turnId: string
    }) => Promise<{ approved: boolean; reason?: string }>
  } = {
    config: deps.config,
    logger: childLogger,
  }
  if (deps.exec !== undefined) restartOpts.exec = deps.exec
  if (deps.approveRestart !== undefined) restartOpts.approveRestart = deps.approveRestart

  // dry_run_install uses an IExecutor (streaming-capable). Defaults to a
  // fresh ChildProcessExecutor when the caller doesn't inject one — same
  // class InstallStage / RunningSetupStage already use.
  const dryRunExecutor: IExecutor = deps.executor ?? new ChildProcessExecutor()
  const dryRunOpts: { logger: Logger; executor: IExecutor } = {
    logger: childLogger,
    executor: dryRunExecutor,
  }

  // Self-heal tools (card D2). Each callback is optional; when absent the tool
  // still registers but reports its dependency is unavailable rather than
  // throwing. The composition root always supplies all three in production.
  const getRuntimeSpec =
    deps.getRuntimeSpec ?? ((): { version: string; runtimeSpec: unknown } | null => null)
  const listBootIssues = deps.listBootIssues ?? ((): BootIssue[] => [])
  const triggerRebootstrap =
    deps.triggerRebootstrap ??
    ((_reason: string): void => {
      childLogger.warn(
        'request_rebootstrap invoked but no triggerRebootstrap callback was wired',
      )
    })

  return [
    buildProposeRuntimeSpec(proposeOpts),
    buildRestartService(restartOpts),
    buildDryRunInstall(dryRunOpts),
    buildGetRuntimeSpec({ logger: childLogger, getRuntimeSpec }),
    buildGetBootIssues({ logger: childLogger, listBootIssues }),
    buildGetPreviewUrl({
      logger: childLogger,
      ...(deps.getPreviewEnv !== undefined ? { getPreviewEnv: deps.getPreviewEnv } : {}),
    }),
    buildRequestRebootstrap({ logger: childLogger, triggerRebootstrap }),
  ]
}

function validateAgainstSchema(
  schema: object,
  value: unknown,
): { ok: true } | { ok: false; errors: string } {
  const validator = ajv.compile(schema)
  if (validator(value)) return { ok: true }
  const msg = (validator.errors ?? [])
    .map((e) => `${e.instancePath || '<root>'}: ${e.message ?? 'invalid'}`)
    .join('; ')
  return { ok: false, errors: msg }
}

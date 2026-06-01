// AttachmentStager — chat-file-attachments Card 5.
//
// Card 4 (backend) ships a `StageAttachmentPayload` over SignalR the moment a
// browser-side R2 upload completes. This module is the daemon's half of that
// handshake:
//
//   1. Validate the payload (defence-in-depth — the wire is typed, but a
//      schema mismatch from a server-side bug shouldn't crash the daemon).
//   2. Idempotency short-circuit: if `localPath` already exists with bytes,
//      the file is already staged — most likely a duplicate push or a daemon
//      respawn after the original ack was lost. Ack success and return.
//   3. Otherwise: ensure the parent directory exists, stream the file from
//      the presigned R2 GET URL into `localPath`. The stream is piped — large
//      files never sit in memory.
//   4. Ack the backend via `RuntimeHub.ReportAttachmentStaged(id, ok, error?)`.
//      Success carries `null` for error; failure carries enough context for an
//      operator to triage.
//
// === Why no retries here ===
//
// The spec is explicit: the daemon is best-effort. A failed stage flips the
// frontend chip to "Runtime download failed" with a Retry button, which
// re-POSTs `/complete` on the backend and triggers a fresh push. Adding a
// daemon-side retry loop would just add a layer of latency on top of the
// user-visible one without changing semantics.
//
// === Why no size enforcement ===
//
// Backend enforces the 50 MB cap at presign time (the only path to a download
// URL). Re-checking here would just duplicate that enforcement; a "tampered
// daemon" threat model doesn't exist because the daemon already controls the
// runtime filesystem.
//
// === Partial-file cleanup ===
//
// If the fetch stream errors mid-download, `localPath` could be left as a
// half-written file. A retry would then see a non-zero file and short-circuit
// the idempotency check, leaving the agent reading a truncated attachment. We
// explicitly `rm` on any failure path so the next push starts clean.

import { createWriteStream } from 'node:fs'
import { mkdir, rm, stat } from 'node:fs/promises'
import path from 'node:path'
import { Readable } from 'node:stream'
import { pipeline } from 'node:stream/promises'

import type { Logger } from 'pino'

import type { StageAttachmentPayload } from '../generated/signalr/Source.Features.SignalR.Contracts.js'

/**
 * Narrow surface the stager needs from the SignalR hub. Constructor-injected
 * (not the whole `SignalRClient`) so tests can pass a hand-rolled fake without
 * mocking the entire wire surface.
 *
 * The wire method is `RuntimeHub.ReportAttachmentStaged(attachmentId, success,
 * error?)`. The generated proxy types `error` as a non-null string, but the
 * .NET signature is `string?` — invoking via the daemon's generic `invoke()`
 * escape hatch lets us pass `null` cleanly when there is no error.
 */
export interface AttachmentStagerHub {
  invoke(
    method: 'ReportAttachmentStaged',
    attachmentId: string,
    success: boolean,
    error: string | null,
  ): Promise<void>
}

/**
 * Test seam — defaults to `globalThis.fetch`. Same `typeof fetch` pattern used
 * by `BootstrapEnvStage` so we don't have to `vi.mock('node:undici')`.
 */
export type FetchImpl = typeof fetch

export interface AttachmentStagerDeps {
  hub: AttachmentStagerHub
  logger: Logger
  fetchImpl?: FetchImpl
}

export class AttachmentStager {
  readonly #hub: AttachmentStagerHub
  readonly #logger: Logger
  readonly #fetchImpl: FetchImpl

  constructor(deps: AttachmentStagerDeps) {
    this.#hub = deps.hub
    this.#logger = deps.logger.child({ module: 'attachment-stager' })
    // Bind the global so it doesn't trip on `this === undefined` in some Node
    // versions. Matches BootstrapEnvStage.
    this.#fetchImpl = deps.fetchImpl ?? ((...args) => fetch(...args))
  }

  /**
   * Download and stage one attachment. Never throws — every failure path goes
   * through `#reportFailure` so the backend always gets an ack. SignalR
   * already wraps inbound dispatch in a try/catch (see
   * `SignalRClient.#guardAndDispatch`), but treating the contract as
   * "always-ack" inside this class keeps the report-back path centralised.
   */
  async stage(payload: StageAttachmentPayload): Promise<void> {
    const validation = validatePayload(payload)
    if (!validation.ok) {
      this.#logger.error(
        { reason: validation.reason, payload: redactPayload(payload) },
        'invalid StageAttachment payload',
      )
      // No attachmentId we can trust → can't ack. Best we can do is log; the
      // frontend timeout will surface the failure to the user.
      const idForAck =
        typeof payload?.attachmentId === 'string' && payload.attachmentId.length > 0
          ? payload.attachmentId
          : null
      if (idForAck !== null) {
        await this.#reportFailure(idForAck, `invalid payload: ${validation.reason}`)
      }
      return
    }

    const { attachmentId, conversationId, fileName, downloadUrl, localPath } = payload
    const ctx = { attachmentId, conversationId, fileName, localPath }
    const startedAt = Date.now()

    // ---- Idempotency check -------------------------------------------------
    // `fs.stat` is the cheapest probe. ENOENT → file doesn't exist → proceed
    // with download. A zero-byte file is treated as "not staged" so a previous
    // half-written attempt that happened to land at 0 bytes can be retried.
    try {
      const existing = await stat(localPath)
      if (existing.isFile() && existing.size > 0) {
        this.#logger.info(
          { ...ctx, bytes: existing.size },
          'attachment already staged, skipping download',
        )
        await this.#reportSuccess(attachmentId)
        return
      }
    } catch (err) {
      // ENOENT is the expected branch — proceed. Any other error (EACCES, …)
      // we let the subsequent mkdir/write surface; logging here would be
      // double-noise.
      void err
    }

    this.#logger.info(ctx, 'staging attachment')

    // ---- Ensure parent directory exists -----------------------------------
    try {
      await mkdir(path.dirname(localPath), { recursive: true })
    } catch (err) {
      const reason = err instanceof Error ? err.message : String(err)
      this.#logger.error({ ...ctx, err }, 'mkdir failed')
      await this.#reportFailure(attachmentId, `mkdir failed: ${reason}`)
      return
    }

    // ---- Fetch + stream to disk ------------------------------------------
    let response: Response
    try {
      response = await this.#fetchImpl(downloadUrl)
    } catch (err) {
      const reason = err instanceof Error ? err.message : String(err)
      this.#logger.error({ ...ctx, err }, 'fetch threw')
      await this.#cleanupPartial(localPath)
      await this.#reportFailure(attachmentId, `fetch failed: ${reason}`)
      return
    }

    if (!response.ok) {
      const reason = `HTTP ${response.status} ${response.statusText}`
      this.#logger.error({ ...ctx, status: response.status }, 'fetch returned non-OK')
      await this.#cleanupPartial(localPath)
      await this.#reportFailure(attachmentId, reason)
      return
    }

    if (response.body === null) {
      this.#logger.error({ ...ctx }, 'fetch returned empty body')
      await this.#cleanupPartial(localPath)
      await this.#reportFailure(attachmentId, 'fetch returned empty body')
      return
    }

    try {
      // `Readable.fromWeb` converts the WHATWG ReadableStream to a Node
      // Readable so `pipeline` can drive it. `pipeline` handles backpressure
      // and propagates errors from either end.
      await pipeline(
        Readable.fromWeb(response.body as Parameters<typeof Readable.fromWeb>[0]),
        createWriteStream(localPath),
      )
    } catch (err) {
      const reason = err instanceof Error ? err.message : String(err)
      this.#logger.error({ ...ctx, err }, 'stream to disk failed')
      await this.#cleanupPartial(localPath)
      await this.#reportFailure(attachmentId, `stream failed: ${reason}`)
      return
    }

    // ---- Success ----------------------------------------------------------
    let bytes: number | null = null
    try {
      const written = await stat(localPath)
      bytes = written.size
    } catch {
      // Stat after a successful write should never fail, but if it does we
      // still want to ack success — the write itself completed.
    }

    this.#logger.info(
      { ...ctx, bytes, durationMs: Date.now() - startedAt },
      'attachment staged',
    )
    await this.#reportSuccess(attachmentId)
  }

  // ============================================================================
  // Private helpers
  // ============================================================================

  async #reportSuccess(attachmentId: string): Promise<void> {
    try {
      await this.#hub.invoke('ReportAttachmentStaged', attachmentId, true, null)
    } catch (err) {
      // Hub blip — log and move on. The backend's source of truth is
      // `Attachment.StagedAt`; a missed ack here will result in a stuck
      // chip until the user retries (which re-pushes), which is the
      // documented v1 behaviour.
      this.#logger.warn({ err, attachmentId }, 'ReportAttachmentStaged(success) invoke failed')
    }
  }

  async #reportFailure(attachmentId: string, errorMessage: string): Promise<void> {
    try {
      await this.#hub.invoke(
        'ReportAttachmentStaged',
        attachmentId,
        false,
        errorMessage,
      )
    } catch (err) {
      this.#logger.warn(
        { err, attachmentId, errorMessage },
        'ReportAttachmentStaged(failure) invoke failed',
      )
    }
  }

  async #cleanupPartial(localPath: string): Promise<void> {
    // `force: true` makes ENOENT a no-op — the common case is "fetch errored
    // before the write stream was even created", in which case there's nothing
    // to clean up. A leftover file on retry would falsely trip the idempotency
    // short-circuit; this defends against that.
    try {
      await rm(localPath, { force: true })
    } catch (err) {
      this.#logger.warn({ err, localPath }, 'partial-file cleanup failed (ignored)')
    }
  }
}

// ============================================================================
// Payload validation — defence-in-depth. The wire is typed via the generated
// proxy, but a server-side bug or a future schema drift shouldn't crash the
// daemon's SignalR pipeline. Hand-rolled rather than dragging zod in for five
// strings — keeps the bundle small + the validation explicit.
// ============================================================================

type ValidationResult =
  | { ok: true }
  | { ok: false; reason: string }

function validatePayload(payload: unknown): ValidationResult {
  if (payload === null || typeof payload !== 'object') {
    return { ok: false, reason: 'payload is not an object' }
  }
  const p = payload as Record<string, unknown>
  const requiredFields = [
    'attachmentId',
    'conversationId',
    'fileName',
    'downloadUrl',
    'localPath',
  ] as const
  for (const field of requiredFields) {
    const value = p[field]
    if (typeof value !== 'string' || value.length === 0) {
      return { ok: false, reason: `${field} missing or empty` }
    }
  }
  return { ok: true }
}

/**
 * Strip the presigned `downloadUrl` from a payload before it goes into a log
 * line. The URL contains a short-lived SigV4 signature; while it's not a
 * long-term secret, treating signed URLs as sensitive is good hygiene.
 */
function redactPayload(payload: unknown): Record<string, unknown> {
  if (payload === null || typeof payload !== 'object') {
    return { payload: '<non-object>' }
  }
  const p = payload as Record<string, unknown>
  return {
    attachmentId: p.attachmentId,
    conversationId: p.conversationId,
    fileName: p.fileName,
    localPath: p.localPath,
    downloadUrl: typeof p.downloadUrl === 'string' ? '<redacted-presigned-url>' : p.downloadUrl,
  }
}

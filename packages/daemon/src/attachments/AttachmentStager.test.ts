// AttachmentStager tests — hermetic via a fake `fetchImpl` + a per-test
// tmpdir for the real fs writes. Following the pattern set by
// BootstrapEnvStage.test.ts (inject fetch, real `Response` bodies) and
// InstallHashStore.test.ts (real fs, tmpdir cleanup).
//
// Why real fs vs mocked: the stager's primary contract is "the file lands on
// disk at `localPath` with the right bytes" — mocking `createWriteStream` and
// `pipeline` would test our mocks rather than the contract. Real writes into
// `os.tmpdir()` are fast and the per-test cleanup keeps the box tidy.

import { mkdtemp, readFile, rm, stat, writeFile } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import path from 'node:path'
import { Readable } from 'node:stream'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { Logger } from 'pino'

import { AttachmentStager, type AttachmentStagerHub } from './AttachmentStager.js'
import type { StageAttachmentPayload } from '../generated/signalr/Source.Features.SignalR.Contracts.js'

// ============================================================================
// Fixtures
// ============================================================================

function makeLogger() {
  const log = {
    trace: vi.fn(),
    debug: vi.fn(),
    info: vi.fn(),
    warn: vi.fn(),
    error: vi.fn(),
    fatal: vi.fn(),
    child: vi.fn(() => log),
  }
  return log
}

function makeHub() {
  // Typed against the hub's invoke signature directly so `invoke.mock.calls`
  // is a tuple of the right element types and indexing into it typechecks.
  const invoke: ReturnType<typeof vi.fn<AttachmentStagerHub['invoke']>> = vi.fn(
    async () => undefined,
  )
  const hub: AttachmentStagerHub = { invoke }
  return { hub, invoke }
}

function makePayload(overrides: Partial<StageAttachmentPayload> & { localPath: string }): StageAttachmentPayload {
  return {
    attachmentId: '11111111-1111-1111-1111-111111111111',
    conversationId: '22222222-2222-2222-2222-222222222222',
    fileName: 'report.pdf',
    downloadUrl: 'https://r2.example.com/presigned?sig=abc',
    ...overrides,
  }
}

/**
 * Build an `AttachmentStager` with a controllable fake fetch. The returned
 * `tmpRoot` is the directory all tests should put files under; it is cleaned
 * up in `afterEach`.
 */
async function setup(opts?: {
  fetchImpl?: typeof fetch
}) {
  const tmpRoot = await mkdtemp(path.join(tmpdir(), 'attachment-stager-'))
  const logger = makeLogger()
  const { hub, invoke } = makeHub()
  const stager = new AttachmentStager({
    hub,
    logger: logger as unknown as Logger,
    fetchImpl: opts?.fetchImpl ?? (vi.fn() as unknown as typeof fetch),
  })
  return { stager, tmpRoot, logger, hub, invoke }
}

let _toCleanup: string[] = []
beforeEach(() => {
  _toCleanup = []
})
afterEach(async () => {
  for (const dir of _toCleanup) {
    await rm(dir, { recursive: true, force: true }).catch(() => undefined)
  }
})
function track(dir: string): string {
  _toCleanup.push(dir)
  return dir
}

// Helper: produce a fake fetch that returns a Response with `bytes` as the
// body and a 200 status.
function ok200(bytes: Buffer | string): typeof fetch {
  const buf = typeof bytes === 'string' ? Buffer.from(bytes, 'utf8') : bytes
  return (vi.fn(async () => new Response(new Uint8Array(buf), { status: 200 }))) as unknown as typeof fetch
}

// ============================================================================
// Tests
// ============================================================================

describe('AttachmentStager', () => {
  it('happy path: downloads body, writes to localPath, acks success', async () => {
    const fetchImpl = ok200('hello world')
    const { stager, tmpRoot, hub, invoke } = await setup({ fetchImpl })
    track(tmpRoot)
    const localPath = path.join(tmpRoot, 'conv-1', 'att-1-report.pdf')
    const payload = makePayload({ localPath })

    await stager.stage(payload)

    // File is on disk with the expected content.
    const written = await readFile(localPath, 'utf8')
    expect(written).toBe('hello world')

    // Backend got a success ack (id, true, null).
    expect(invoke).toHaveBeenCalledTimes(1)
    expect(invoke).toHaveBeenCalledWith(
      'ReportAttachmentStaged',
      payload.attachmentId,
      true,
      null,
    )
    void hub // silence "unused"
  })

  it('HTTP failure (404): no file written, acks failure with status', async () => {
    const fetchImpl = (vi.fn(async () => new Response('not found', { status: 404, statusText: 'Not Found' }))) as unknown as typeof fetch
    const { stager, tmpRoot, invoke } = await setup({ fetchImpl })
    track(tmpRoot)
    const localPath = path.join(tmpRoot, 'conv-2', 'att-2-x.bin')
    const payload = makePayload({ localPath, attachmentId: 'aaaa-fail-http' })

    await stager.stage(payload)

    // No file on disk.
    await expect(stat(localPath)).rejects.toMatchObject({ code: 'ENOENT' })

    // Failure ack carries the status in the error string.
    expect(invoke).toHaveBeenCalledTimes(1)
    const call = invoke.mock.calls[0]!
    expect(call[0]).toBe('ReportAttachmentStaged')
    expect(call[1]).toBe('aaaa-fail-http')
    expect(call[2]).toBe(false)
    expect(call[3]).toMatch(/404/)
  })

  it('network/fetch throw: no file written, acks failure with error message', async () => {
    const fetchImpl = (vi.fn(async () => {
      throw new Error('ECONNREFUSED')
    })) as unknown as typeof fetch
    const { stager, tmpRoot, invoke } = await setup({ fetchImpl })
    track(tmpRoot)
    const localPath = path.join(tmpRoot, 'conv-3', 'att-3.bin')
    const payload = makePayload({ localPath, attachmentId: 'aaaa-fail-net' })

    await stager.stage(payload)

    await expect(stat(localPath)).rejects.toMatchObject({ code: 'ENOENT' })

    expect(invoke).toHaveBeenCalledTimes(1)
    const call = invoke.mock.calls[0]!
    expect(call[2]).toBe(false)
    expect(call[3]).toMatch(/ECONNREFUSED/)
  })

  it('idempotency: localPath already exists with bytes → skip download, ack success', async () => {
    const fetchImpl = vi.fn(async () => new Response('should-not-be-called', { status: 200 })) as unknown as typeof fetch
    const { stager, tmpRoot, invoke } = await setup({ fetchImpl })
    track(tmpRoot)
    const localPath = path.join(tmpRoot, 'conv-4', 'att-4-existing.txt')
    // Pre-stage the file as if a previous push had already landed it.
    const { mkdir } = await import('node:fs/promises')
    await mkdir(path.dirname(localPath), { recursive: true })
    await writeFile(localPath, 'already-here', 'utf8')

    const payload = makePayload({ localPath, attachmentId: 'aaaa-idem' })
    await stager.stage(payload)

    // Fetch was NOT called.
    expect(fetchImpl).not.toHaveBeenCalled()

    // File content unchanged.
    expect(await readFile(localPath, 'utf8')).toBe('already-here')

    // Success ack went out.
    expect(invoke).toHaveBeenCalledTimes(1)
    expect(invoke).toHaveBeenCalledWith(
      'ReportAttachmentStaged',
      'aaaa-idem',
      true,
      null,
    )
  })

  it('partial-file cleanup: stream errors mid-download → localPath does not exist after', async () => {
    // Build a Response whose body is a Node Readable that emits some bytes
    // then errors. The `Response` constructor accepts a WHATWG ReadableStream
    // — we adapt via `Readable.toWeb`. Errors propagate through `pipeline`
    // to the write stream, triggering our cleanup path.
    const errStream = new Readable({
      read() {
        this.push(Buffer.from('partial-bytes-'))
        // Schedule the error on the next tick so the first chunk has a
        // chance to flush into the pipeline.
        setImmediate(() => this.destroy(new Error('stream torn down')))
      },
    })
    const webStream = Readable.toWeb(errStream) as unknown as ReadableStream<Uint8Array>
    const fetchImpl = (vi.fn(async () => new Response(webStream, { status: 200 }))) as unknown as typeof fetch

    const { stager, tmpRoot, invoke } = await setup({ fetchImpl })
    track(tmpRoot)
    const localPath = path.join(tmpRoot, 'conv-5', 'att-5-flaky.bin')
    const payload = makePayload({ localPath, attachmentId: 'aaaa-partial' })

    await stager.stage(payload)

    // Critical: the partial file must NOT exist — otherwise a retry would
    // falsely trip the idempotency short-circuit.
    await expect(stat(localPath)).rejects.toMatchObject({ code: 'ENOENT' })

    // Failure ack went out with a stream-related message.
    expect(invoke).toHaveBeenCalledTimes(1)
    const call = invoke.mock.calls[0]!
    expect(call[1]).toBe('aaaa-partial')
    expect(call[2]).toBe(false)
    expect(call[3]).toMatch(/stream/i)
  })
})

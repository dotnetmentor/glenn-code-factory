// Tests for InstallHashStore. Hand-rolled fs fakes so nothing touches real
// disk; the store is the only consumer of these fs functions so the seam is
// clean.

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import type { RuntimeSpecV2 } from '../signalr/types.js'
import {
  composeBlob,
  computeSpecHashes,
  InstallHashStore,
  sha256Hex,
  type InstallHashStoreFs,
} from './InstallHashStore.js'

interface FsCall {
  op: 'readFile' | 'writeFile' | 'rename'
  path: string
  contents?: string
}

function makeFs(opts: {
  readContents?: string
  readError?: NodeJS.ErrnoException
  failOn?: 'writeFile' | 'rename'
} = {}): { fs: InstallHashStoreFs; calls: FsCall[] } {
  const calls: FsCall[] = []
  const fs: InstallHashStoreFs = {
    readFile: vi.fn(async (path: unknown) => {
      calls.push({ op: 'readFile', path: String(path) })
      if (opts.readError !== undefined) throw opts.readError
      if (opts.readContents !== undefined) return opts.readContents
      const err = Object.assign(new Error('ENOENT'), { code: 'ENOENT' })
      throw err
    }) as unknown as InstallHashStoreFs['readFile'],
    writeFile: vi.fn(async (path: unknown, contents: unknown) => {
      calls.push({ op: 'writeFile', path: String(path), contents: String(contents) })
      if (opts.failOn === 'writeFile') throw new Error('EACCES writeFile')
    }) as unknown as InstallHashStoreFs['writeFile'],
    rename: vi.fn(async (from: unknown, to: unknown) => {
      calls.push({ op: 'rename', path: `${String(from)}->${String(to)}` })
      if (opts.failOn === 'rename') throw new Error('EXDEV rename')
    }) as unknown as InstallHashStoreFs['rename'],
  }
  return { fs, calls }
}

describe('sha256Hex', () => {
  it('is deterministic across calls', () => {
    expect(sha256Hex('hello world')).toBe(sha256Hex('hello world'))
  })
  it('produces the canonical SHA-256 for the empty string', () => {
    // SHA-256("") - well-known constant.
    expect(sha256Hex('')).toBe(
      'e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855',
    )
  })
  it('changes when input changes by even one byte', () => {
    expect(sha256Hex('foo')).not.toBe(sha256Hex('Foo'))
  })
})

describe('composeBlob', () => {
  it('returns empty string when spec has no install fields', () => {
    const spec: RuntimeSpecV2 = { version: 2 }
    expect(composeBlob(spec)).toBe('')
  })

  it('top-level only — adds a trailing newline if missing', () => {
    const spec: RuntimeSpecV2 = { version: 2, install: 'apt-get install -y curl' }
    expect(composeBlob(spec)).toBe('apt-get install -y curl\n')
  })

  it('top-level only — preserves an existing trailing newline (no double \\n)', () => {
    const spec: RuntimeSpecV2 = { version: 2, install: 'apt-get install -y curl\n' }
    expect(composeBlob(spec)).toBe('apt-get install -y curl\n')
  })

  it('orders top-level first, then services in array order', () => {
    const spec: RuntimeSpecV2 = {
      version: 2,
      install: 'echo top',
      services: [
        { name: 'mongo', command: 'mongod', install: 'echo mongo-install' },
        { name: 'redis', command: 'redis-server', install: 'echo redis-install' },
      ],
    }
    expect(composeBlob(spec)).toBe('echo top\necho mongo-install\necho redis-install\n')
  })

  it('skips empty/whitespace-only chunks', () => {
    const spec: RuntimeSpecV2 = {
      version: 2,
      install: '   \n  \n',
      services: [
        { name: 'a', command: 'cmd', install: 'echo a' },
        { name: 'b', command: 'cmd' },
        { name: 'c', command: 'cmd', install: '' },
        { name: 'd', command: 'cmd', install: 'echo d' },
      ],
    }
    expect(composeBlob(spec)).toBe('echo a\necho d\n')
  })

  it('preserves service-array order so the hash is stable across boots', () => {
    const spec1: RuntimeSpecV2 = {
      version: 2,
      services: [
        { name: 'a', command: 'cmd', install: 'echo a' },
        { name: 'b', command: 'cmd', install: 'echo b' },
      ],
    }
    const spec2: RuntimeSpecV2 = {
      version: 2,
      services: [
        { name: 'b', command: 'cmd', install: 'echo b' },
        { name: 'a', command: 'cmd', install: 'echo a' },
      ],
    }
    // Order changes blob → hash differs. Documenting the invariant.
    expect(composeBlob(spec1)).not.toBe(composeBlob(spec2))
  })
})

describe('computeSpecHashes', () => {
  it('hashes top-level + each service independently', () => {
    const spec: RuntimeSpecV2 = {
      version: 2,
      install: 'echo top',
      services: [
        { name: 'mongo', command: 'mongod', install: 'echo mongo' },
        { name: 'redis', command: 'redis-server' },
      ],
    }
    const hashes = computeSpecHashes(spec)
    expect(hashes.topLevel).toBe(sha256Hex('echo top'))
    expect(hashes.services['mongo']).toBe(sha256Hex('echo mongo'))
    expect(hashes.services['redis']).toBe(sha256Hex(''))
  })

  it('returns empty-string hash for top-level when spec.install absent', () => {
    const spec: RuntimeSpecV2 = { version: 2 }
    expect(computeSpecHashes(spec).topLevel).toBe(sha256Hex(''))
  })
})

describe('InstallHashStore', () => {
  beforeEach(() => vi.useFakeTimers())
  afterEach(() => {
    vi.useRealTimers()
    vi.restoreAllMocks()
  })

  it('read/write roundtrip preserves the hash snapshot', async () => {
    const { fs } = makeFs()
    const store = new InstallHashStore({ path: '/tmp/hashes.json', fs })
    const snapshot = {
      topLevel: 'aaa',
      services: { mongo: 'mhash', redis: 'rhash' },
    }
    await store.write(snapshot)

    // Subsequent read returns what was written. We rewire the readFile
    // fake to return the tmp file's contents — since the fs fake records
    // the write payload, we plumb it through.
    const writeCall = (fs.writeFile as ReturnType<typeof vi.fn>).mock.calls[0]
    const writtenBody = String(writeCall?.[1])
    const fs2 = makeFs({ readContents: writtenBody })
    const store2 = new InstallHashStore({ path: '/tmp/hashes.json', fs: fs2.fs })
    const got = await store2.read()
    expect(got).toEqual(snapshot)
  })

  it('read() returns empty hashes when file does not exist (ENOENT)', async () => {
    const { fs } = makeFs() // default makeFs throws ENOENT
    const store = new InstallHashStore({ path: '/tmp/hashes.json', fs })
    const got = await store.read()
    expect(got).toEqual({ topLevel: '', services: {} })
  })

  it('read() returns empty hashes when JSON is malformed', async () => {
    const { fs } = makeFs({ readContents: '{ not valid json' })
    const store = new InstallHashStore({ path: '/tmp/hashes.json', fs })
    const got = await store.read()
    expect(got).toEqual({ topLevel: '', services: {} })
  })

  it('read() returns empty hashes when JSON shape is wrong', async () => {
    const { fs } = makeFs({
      readContents: JSON.stringify({ topLevel: 42, services: 'nope' }),
    })
    const store = new InstallHashStore({ path: '/tmp/hashes.json', fs })
    const got = await store.read()
    expect(got).toEqual({ topLevel: '', services: {} })
  })

  it('read() returns empty hashes when any service hash is not a string', async () => {
    const { fs } = makeFs({
      readContents: JSON.stringify({
        topLevel: 'good',
        services: { mongo: 'mhash', redis: 12345 },
      }),
    })
    const store = new InstallHashStore({ path: '/tmp/hashes.json', fs })
    const got = await store.read()
    expect(got).toEqual({ topLevel: '', services: {} })
  })

  it('write() goes through a .tmp file and renames atomically', async () => {
    const { fs, calls } = makeFs()
    const store = new InstallHashStore({ path: '/tmp/hashes.json', fs })
    await store.write({ topLevel: 'a', services: { x: 'b' } })

    // Order: writeFile to .tmp first, then rename.
    const ops = calls.map((c) => c.op)
    expect(ops).toEqual(['writeFile', 'rename'])
    expect(calls[0]?.path).toBe('/tmp/hashes.json.tmp')
    expect(calls[1]?.path).toBe('/tmp/hashes.json.tmp->/tmp/hashes.json')
  })

  it('write() propagates fs errors (caller decides what to do)', async () => {
    const { fs } = makeFs({ failOn: 'rename' })
    const store = new InstallHashStore({ path: '/tmp/hashes.json', fs })
    await expect(
      store.write({ topLevel: 'a', services: {} }),
    ).rejects.toThrow(/EXDEV rename/)
  })

  it('read() tolerates non-ENOENT read errors and returns empty', async () => {
    const eio = Object.assign(new Error('EIO'), { code: 'EIO' }) as NodeJS.ErrnoException
    const { fs } = makeFs({ readError: eio })
    const store = new InstallHashStore({ path: '/tmp/hashes.json', fs })
    const got = await store.read()
    expect(got).toEqual({ topLevel: '', services: {} })
  })
})

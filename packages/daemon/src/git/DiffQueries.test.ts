// Tests for DiffQueries — Phase 1 of the diff-view-tab spec.
//
// Two layers of coverage:
//
//   1. Pure parser tests (`parsePorcelainV2`, `parseNumstat`) — exercise
//      every record kind we handle (modified, added, deleted, renamed,
//      untracked, submodule, ignored, unmerged) against hand-rolled
//      NUL/tab-formatted strings. These are the bits most likely to break
//      when git changes its output shape; they're deterministic and run
//      in microseconds.
//
//   2. End-to-end tests against a real temp git repo. Same approach as
//      SshKeyHandler.test.ts — `mkdtemp` under `os.tmpdir()`, run real
//      `git` to set up a known state, then call the public functions and
//      assert on the returned shape. Covers the body-clipping path
//      (>500 KB diff) and the binary-file path because both rely on
//      stdout from git that's painful to fake at the right fidelity.
//
// We deliberately do NOT mock `child_process.spawn`. The whole point of
// `runGit` is to absorb git's quirks (exit codes, NUL termination, the
// `Binary files differ` line); a spawn mock would let us paper over those.

import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { spawn } from 'node:child_process'
import * as realFs from 'node:fs/promises'
import * as os from 'node:os'
import path from 'node:path'

import {
  InvalidGitRefError,
  MAX_DIFF_BODY_BYTES,
  getBranchChangedFiles,
  getBranchFileDiff,
  getCommitRange,
  getWorkingTreeChangedFiles,
  getWorkingTreeFileDiff,
  looksLikePlainBranchName,
  parseNumstat,
  parsePorcelainV2,
} from './DiffQueries.js'

// ============================================================================
// Helpers — real-repo bring-up
// ============================================================================

let repoDir: string

/**
 * Run a one-shot git command in `cwd`, fail the test loudly on non-zero exit
 * (the bring-up must always succeed; if it doesn't the test result is
 * meaningless). Used for `git init`, `git add`, `git commit` etc. — not for
 * exercising the code under test.
 */
async function git(cwd: string, args: string[]): Promise<void> {
  await new Promise<void>((resolve, reject) => {
    const child = spawn('/usr/bin/git', args, {
      cwd,
      env: {
        ...process.env,
        GIT_AUTHOR_NAME: 'Test',
        GIT_AUTHOR_EMAIL: 'test@example.com',
        GIT_COMMITTER_NAME: 'Test',
        GIT_COMMITTER_EMAIL: 'test@example.com',
        GIT_TERMINAL_PROMPT: '0',
      },
      stdio: ['ignore', 'pipe', 'pipe'],
    })
    let stderr = ''
    child.stderr.on('data', (c: Buffer) => {
      stderr += c.toString('utf8')
    })
    child.on('error', reject)
    child.on('close', (code) => {
      if (code === 0) resolve()
      else reject(new Error(`git ${args.join(' ')} exited ${String(code)}: ${stderr}`))
    })
  })
}

beforeEach(async () => {
  repoDir = await realFs.mkdtemp(path.join(os.tmpdir(), 'diff-queries-'))
  await git(repoDir, ['init', '-q', '-b', 'main'])
  // Force a stable identity even if the host has a different config.
  await git(repoDir, ['config', 'user.name', 'Test'])
  await git(repoDir, ['config', 'user.email', 'test@example.com'])
  // Disable commit/tag signing repo-locally. Some environments (e.g. CI
  // sandboxes) set `commit.gpgsign=true` with a host-managed signer that
  // isn't usable here, which would make every `git commit` exit 128. These
  // tests assert git's diff/log *output shape*, not signatures, so signing is
  // pure friction — turn it off so the suite is portable across hosts.
  await git(repoDir, ['config', 'commit.gpgsign', 'false'])
  await git(repoDir, ['config', 'tag.gpgsign', 'false'])
})

afterEach(async () => {
  await realFs.rm(repoDir, { recursive: true, force: true })
})

async function commit(message: string): Promise<void> {
  await git(repoDir, ['add', '-A'])
  await git(repoDir, ['commit', '-q', '-m', message])
}

/**
 * Capture the SHA at HEAD (or any ref) so the test can plant it elsewhere
 * — e.g. under `refs/remotes/origin/main` to simulate a partial clone where
 * the only trace of `main` is the remote-tracking ref.
 */
async function readSha(cwd: string, ref: string = 'HEAD'): Promise<string> {
  return await new Promise<string>((resolve, reject) => {
    const child = spawn('/usr/bin/git', ['rev-parse', ref], { cwd })
    let out = ''
    child.stdout.on('data', (c: Buffer) => {
      out += c.toString('utf8')
    })
    child.on('error', reject)
    child.on('close', (code) =>
      code === 0 ? resolve(out.trim()) : reject(new Error(`rev-parse ${ref} failed (code ${String(code)})`)),
    )
  })
}

// ============================================================================
// parsePorcelainV2
// ============================================================================

describe('parsePorcelainV2', () => {
  it('parses a modified ordinary entry', () => {
    // "1 .M N... 100644 100644 100644 <hH> <hI> path"
    // X='.' (no staged change), Y='M' (modified in working tree).
    const raw = '1 .M N... 100644 100644 100644 abc def src/file.ts\0'
    const out = parsePorcelainV2(raw)
    expect(out).toHaveLength(1)
    expect(out[0]).toEqual({
      status: 'modified',
      path: 'src/file.ts',
      oldPath: null,
      isSubmodule: false,
    })
  })

  it('parses an added (staged) entry', () => {
    // X='A' (added in index), Y='.'.
    const raw = '1 A. N... 000000 100644 100644 abc def newfile.txt\0'
    const out = parsePorcelainV2(raw)
    expect(out).toEqual([
      { status: 'added', path: 'newfile.txt', oldPath: null, isSubmodule: false },
    ])
  })

  it('parses a deleted entry', () => {
    const raw = '1 D. N... 100644 100644 000000 abc def gone.txt\0'
    const out = parsePorcelainV2(raw)
    expect(out).toEqual([
      { status: 'deleted', path: 'gone.txt', oldPath: null, isSubmodule: false },
    ])
  })

  it('parses a renamed entry — newPath in record, oldPath in next NUL token', () => {
    // "2 R. N... 100644 100644 100644 <hH> <hI> R100 newPath\0oldPath\0"
    const raw = '2 R. N... 100644 100644 100644 abc def R100 src/new.ts\0src/old.ts\0'
    const out = parsePorcelainV2(raw)
    expect(out).toEqual([
      { status: 'renamed', path: 'src/new.ts', oldPath: 'src/old.ts', isSubmodule: false },
    ])
  })

  it('parses an untracked entry', () => {
    const raw = '? scratch.txt\0'
    const out = parsePorcelainV2(raw)
    expect(out).toEqual([
      { status: 'untracked', path: 'scratch.txt', oldPath: null, isSubmodule: false },
    ])
  })

  it('skips ignored entries', () => {
    const raw = '! node_modules/foo\0'
    const out = parsePorcelainV2(raw)
    expect(out).toEqual([])
  })

  it('detects submodules from the sub field', () => {
    // sub starts with 'S' for submodules.
    const raw = '1 .M S..U 160000 160000 160000 abc def vendor/sub\0'
    const out = parsePorcelainV2(raw)
    expect(out).toEqual([
      { status: 'modified', path: 'vendor/sub', oldPath: null, isSubmodule: true },
    ])
  })

  it('handles unmerged (conflict) entries as modified', () => {
    // "u XY sub <m1> <m2> <m3> <mW> <h1> <h2> <h3> path"
    const raw = 'u UU N... 100644 100644 100644 100644 a b c conflicted.txt\0'
    const out = parsePorcelainV2(raw)
    expect(out).toEqual([
      { status: 'modified', path: 'conflicted.txt', oldPath: null, isSubmodule: false },
    ])
  })

  it('parses a mixed batch of records', () => {
    const raw = [
      '1 .M N... 100644 100644 100644 a b mod.ts',
      '? new.ts',
      '2 R. N... 100644 100644 100644 a b R100 dst.ts\0src.ts',
    ].join('\0') + '\0'
    const out = parsePorcelainV2(raw)
    expect(out.map((e) => e.status)).toEqual(['modified', 'untracked', 'renamed'])
    expect(out[2]?.oldPath).toBe('src.ts')
  })
})

// ============================================================================
// parseNumstat
// ============================================================================

describe('parseNumstat', () => {
  it('parses a normal additions / deletions row', () => {
    const out = parseNumstat('5\t3\tsrc/file.ts\n')
    expect(out.get('src/file.ts')).toEqual({ additions: 5, deletions: 3, isBinary: false })
  })

  it('parses a binary marker (-\\t-)', () => {
    const out = parseNumstat('-\t-\timg/logo.png\n')
    expect(out.get('img/logo.png')).toEqual({ additions: 0, deletions: 0, isBinary: true })
  })

  it('parses multiple rows', () => {
    const raw = '5\t3\ta.ts\n10\t0\tb.ts\n-\t-\tc.png\n'
    const out = parseNumstat(raw)
    expect(out.size).toBe(3)
    expect(out.get('b.ts')?.additions).toBe(10)
    expect(out.get('c.png')?.isBinary).toBe(true)
  })

  it('preserves tabs inside the path', () => {
    // Pathological but legal: a path containing a tab. We rejoin everything
    // after index 2 with `\t` so the path round-trips.
    const out = parseNumstat('1\t1\tweird\tname.txt\n')
    expect(out.get('weird\tname.txt')).toEqual({ additions: 1, deletions: 1, isBinary: false })
  })

  it('returns an empty map for empty input', () => {
    expect(parseNumstat('').size).toBe(0)
  })
})

// ============================================================================
// getWorkingTreeChangedFiles — real repo end-to-end
// ============================================================================

describe('getWorkingTreeChangedFiles', () => {
  it('returns an empty list for a clean repo', async () => {
    await realFs.writeFile(path.join(repoDir, 'a.txt'), 'hello\n')
    await commit('init')

    const out = await getWorkingTreeChangedFiles(repoDir)
    expect(out.scope).toBe('workingTree')
    expect(out.files).toEqual([])
    expect(out.totalAdditions).toBe(0)
    expect(out.totalDeletions).toBe(0)
    expect(out.reason).toBeNull()
  })

  it('reports a modified tracked file with line counts', async () => {
    await realFs.writeFile(path.join(repoDir, 'a.txt'), 'one\ntwo\nthree\n')
    await commit('init')
    await realFs.writeFile(path.join(repoDir, 'a.txt'), 'one\nTWO\nthree\nfour\n')

    const out = await getWorkingTreeChangedFiles(repoDir)
    expect(out.files).toHaveLength(1)
    const file = out.files[0]
    expect(file?.path).toBe('a.txt')
    expect(file?.status).toBe('modified')
    expect(file?.additions).toBeGreaterThan(0)
    expect(file?.deletions).toBeGreaterThan(0)
    expect(file?.isBinary).toBe(false)
    expect(out.totalAdditions).toBe(file?.additions)
  })

  it('reports an untracked file with size', async () => {
    await realFs.writeFile(path.join(repoDir, 'seed.txt'), 'seed\n')
    await commit('init')
    await realFs.writeFile(path.join(repoDir, 'fresh.txt'), 'line1\nline2\n')

    const out = await getWorkingTreeChangedFiles(repoDir)
    const fresh = out.files.find((f) => f.path === 'fresh.txt')
    expect(fresh).toBeDefined()
    expect(fresh?.status).toBe('untracked')
    expect(fresh?.sizeBytes).toBeGreaterThan(0)
  })

  it('reports a deleted file', async () => {
    await realFs.writeFile(path.join(repoDir, 'gone.txt'), 'bye\n')
    await commit('init')
    await realFs.unlink(path.join(repoDir, 'gone.txt'))

    const out = await getWorkingTreeChangedFiles(repoDir)
    const gone = out.files.find((f) => f.path === 'gone.txt')
    expect(gone?.status).toBe('deleted')
    expect(gone?.deletions).toBeGreaterThan(0)
    expect(gone?.sizeBytes).toBeNull()
  })
})

// ============================================================================
// getWorkingTreeFileDiff — body clipping + binary
// ============================================================================

describe('getWorkingTreeFileDiff', () => {
  it('returns unified-diff text for a tracked modified file', async () => {
    await realFs.writeFile(path.join(repoDir, 'a.txt'), 'before\n')
    await commit('init')
    await realFs.writeFile(path.join(repoDir, 'a.txt'), 'after\n')

    const out = await getWorkingTreeFileDiff(repoDir, 'a.txt')
    expect(out.isBinary).toBe(false)
    expect(out.isTruncated).toBe(false)
    expect(out.unifiedDiff ?? '').toContain('@@')
    expect(out.unifiedDiff ?? '').toContain('-before')
    expect(out.unifiedDiff ?? '').toContain('+after')
    expect(out.reason).toBeNull()
  })

  it('returns unified-diff text for an untracked file via --no-index', async () => {
    await realFs.writeFile(path.join(repoDir, 'seed.txt'), 'seed\n')
    await commit('init')
    await realFs.writeFile(path.join(repoDir, 'fresh.txt'), 'fresh content\n')

    const out = await getWorkingTreeFileDiff(repoDir, 'fresh.txt')
    expect(out.isBinary).toBe(false)
    expect(out.unifiedDiff ?? '').toContain('+fresh content')
  })

  it('flags binary files with isBinary=true and reason="binary"', async () => {
    // Commit a non-binary placeholder, then overwrite with bytes that include
    // a NUL byte — git classifies the new content as binary.
    await realFs.writeFile(path.join(repoDir, 'logo.bin'), 'placeholder\n')
    await commit('init')
    const binary = Buffer.from([0x00, 0x01, 0x02, 0x03, 0xff, 0xfe, 0xfd, 0xfc, 0x00, 0x10])
    await realFs.writeFile(path.join(repoDir, 'logo.bin'), binary)

    const out = await getWorkingTreeFileDiff(repoDir, 'logo.bin')
    expect(out.isBinary).toBe(true)
    expect(out.unifiedDiff).toBeNull()
    expect(out.reason).toBe('binary')
  })

  it('clips the body at MAX_DIFF_BODY_BYTES and sets isTruncated=true', async () => {
    // Generate a diff comfortably larger than the cap. Each line is ~80 bytes;
    // we need (cap / 80) lines to exceed; double it for safety. We commit the
    // empty file first so the diff is "all additions" (predictable).
    const filePath = path.join(repoDir, 'big.txt')
    await realFs.writeFile(filePath, '')
    await commit('init')

    const lineCount = Math.ceil(MAX_DIFF_BODY_BYTES / 50) * 2
    const lines: string[] = []
    for (let i = 0; i < lineCount; i++) {
      lines.push(`line ${String(i).padStart(8, '0')} body content padding\n`)
    }
    await realFs.writeFile(filePath, lines.join(''))

    const out = await getWorkingTreeFileDiff(repoDir, 'big.txt')
    expect(out.isBinary).toBe(false)
    expect(out.isTruncated).toBe(true)
    expect(out.reason).toBe('too-large')
    // The clipped body must be at the cap exactly (we slice on bytes).
    const decoded = out.unifiedDiff ?? ''
    const byteLen = Buffer.byteLength(decoded, 'utf8')
    expect(byteLen).toBeLessThanOrEqual(MAX_DIFF_BODY_BYTES)
    // And we must have shipped a meaningful slice — at minimum, the diff
    // header.
    expect(decoded).toContain('@@')
  })
})

// ============================================================================
// getBranchChangedFiles — branch scope (Phase 3)
// ============================================================================

describe('getBranchChangedFiles', () => {
  it('returns +/- counts and status for files added on a feature branch', async () => {
    // Set up: main has a.txt; feature branch adds b.txt + modifies a.txt.
    await realFs.writeFile(path.join(repoDir, 'a.txt'), 'one\ntwo\n')
    await commit('init')

    await git(repoDir, ['checkout', '-q', '-b', 'feature'])
    await realFs.writeFile(path.join(repoDir, 'a.txt'), 'one\nTWO\nthree\n')
    await realFs.writeFile(path.join(repoDir, 'b.txt'), 'new file\n')
    await commit('feature work')

    const out = await getBranchChangedFiles(repoDir, 'main', 'feature')
    expect(out.scope).toBe('branch')
    expect(out.base).toBe('main')
    expect(out.head).toBe('feature')

    const a = out.files.find((f) => f.path === 'a.txt')
    const b = out.files.find((f) => f.path === 'b.txt')
    expect(a?.status).toBe('modified')
    expect(a?.additions).toBeGreaterThan(0)
    expect(b?.status).toBe('added')
    expect(b?.additions).toBeGreaterThan(0)

    expect(out.totalAdditions).toBe(out.files.reduce((s, f) => s + f.additions, 0))
    expect(out.totalDeletions).toBe(out.files.reduce((s, f) => s + f.deletions, 0))
    expect(out.reason).toBeNull()
  })

  it('defaults headRef to HEAD', async () => {
    await realFs.writeFile(path.join(repoDir, 'a.txt'), 'before\n')
    await commit('init')
    await realFs.writeFile(path.join(repoDir, 'a.txt'), 'after\n')
    await commit('change a')

    // We're on main; HEAD is the second commit; compare against the first.
    const initSha = (
      await new Promise<string>((resolve, reject) => {
        const child = spawn('/usr/bin/git', ['rev-parse', 'HEAD~1'], { cwd: repoDir })
        let out = ''
        child.stdout.on('data', (c: Buffer) => {
          out += c.toString('utf8')
        })
        child.on('error', reject)
        child.on('close', (code) => (code === 0 ? resolve(out.trim()) : reject(new Error('rev-parse failed'))))
      })
    )

    const result = await getBranchChangedFiles(repoDir, initSha)
    expect(result.head).toBe('HEAD')
    expect(result.files.some((f) => f.path === 'a.txt')).toBe(true)
  })

  it('throws InvalidGitRefError when baseRef does not exist', async () => {
    await realFs.writeFile(path.join(repoDir, 'a.txt'), 'hi\n')
    await commit('init')

    await expect(getBranchChangedFiles(repoDir, 'does-not-exist')).rejects.toBeInstanceOf(
      InvalidGitRefError,
    )
  })

  it('detects renames as renamed (not delete+add)', async () => {
    await realFs.writeFile(path.join(repoDir, 'old.txt'), 'one\ntwo\nthree\nfour\nfive\n')
    await commit('init')

    await git(repoDir, ['checkout', '-q', '-b', 'feature'])
    await git(repoDir, ['mv', 'old.txt', 'new.txt'])
    // Keep ~similar content so git's rename detector fires (default 50%).
    await realFs.writeFile(path.join(repoDir, 'new.txt'), 'one\ntwo\nthree\nfour\nFIVE\n')
    await commit('rename + tweak')

    const out = await getBranchChangedFiles(repoDir, 'main', 'feature')
    const rename = out.files.find((f) => f.path === 'new.txt')
    expect(rename?.status).toBe('renamed')
    expect(rename?.oldPath).toBe('old.txt')
  })

  // --------------------------------------------------------------------------
  // origin/* fallback — the partial-clone scenario that motivated this fix.
  //
  // GlennCode runtimes only check out the active feature branch (`lab`,
  // `bootstrap`, etc.). The default branch (`main`) lives only as the
  // remote-tracking ref `origin/main`. Without the fallback, the default
  // "Compare against: main" UX would 400 on every non-default branch — the
  // exact bug exposed by the branch→runtime fix.
  // --------------------------------------------------------------------------
  it('resolves bare "main" via origin/main when no local main exists', async () => {
    // Seed main with one file, then capture its SHA so we can plant it
    // under `refs/remotes/origin/main` after deleting the local branch.
    await realFs.writeFile(path.join(repoDir, 'shared.txt'), 'base\n')
    await commit('main: base')
    const mainSha = await readSha(repoDir)

    // Branch off, add a commit, then delete the local `main` branch and
    // re-plant it as a remote-tracking ref only — same shape as a partial
    // clone where the runtime only ever checked out the feature branch.
    await git(repoDir, ['checkout', '-q', '-b', 'feature'])
    await realFs.writeFile(path.join(repoDir, 'shared.txt'), 'feature change\n')
    await realFs.writeFile(path.join(repoDir, 'new-on-feature.txt'), 'hi\n')
    await commit('feature: change shared, add new file')

    // Detach to delete the local main branch safely (can't delete the
    // branch HEAD is currently on; we're on feature so this is fine, but
    // belt-and-braces).
    await git(repoDir, ['branch', '-D', 'main'])
    await git(repoDir, ['update-ref', 'refs/remotes/origin/main', mainSha])

    // The bare name `main` does NOT resolve directly anymore — but
    // `origin/main` does. resolveRef should bridge that gap.
    const out = await getBranchChangedFiles(repoDir, 'main', 'HEAD')

    // The diff IS computed (no throw), and reports the user's original
    // ref names so the UI label "Compare against: main" stays accurate.
    expect(out.base).toBe('main')
    expect(out.head).toBe('HEAD')
    const paths = out.files.map((f) => f.path).sort()
    expect(paths).toEqual(['new-on-feature.txt', 'shared.txt'])
  })

  it('still throws InvalidGitRefError when neither <ref> nor origin/<ref> exists', async () => {
    await realFs.writeFile(path.join(repoDir, 'a.txt'), 'hi\n')
    await commit('init')

    // Bare name, no origin remote at all — fallback can't save us.
    await expect(getBranchChangedFiles(repoDir, 'totally-unknown')).rejects.toBeInstanceOf(
      InvalidGitRefError,
    )
  })

  it('does NOT rewrite qualified refs to origin/<ref>', async () => {
    // Already-qualified refs (`refs/heads/...`, `refs/remotes/...`) are NOT
    // prefixed with `origin/` because that would corrupt the meaning.
    // Verify by asking for a literal `refs/heads/no-such` and confirming we
    // throw with the original ref name, NOT some surprise success via
    // `origin/refs/heads/no-such`.
    await realFs.writeFile(path.join(repoDir, 'a.txt'), 'hi\n')
    await commit('init')

    await expect(
      getBranchChangedFiles(repoDir, 'refs/heads/no-such-branch'),
    ).rejects.toBeInstanceOf(InvalidGitRefError)
  })

  it('resolves slashed branch name `feat/login` via `origin/feat/login`', async () => {
    // Real branches very often live in nested namespaces (`feat/foo`,
    // `release/2025-05`, `dependabot/npm/react-19`). The first version of
    // this fix rejected anything with `/` in the predicate and silently
    // broke the partial-clone fallback for those users. Lock in the
    // corrected behaviour.
    await realFs.writeFile(path.join(repoDir, 'shared.txt'), 'base\n')
    await commit('feat/login: base')
    const baseSha = await readSha(repoDir)

    // Branch off into a fresh feature; the runtime is now "on" that branch.
    await git(repoDir, ['checkout', '-q', '-b', 'current-work'])
    await realFs.writeFile(path.join(repoDir, 'shared.txt'), 'changed\n')
    await commit('current-work: change shared')

    // Plant `origin/feat/login` only — the local `feat/login` branch never
    // existed (this matches `clone --single-branch --branch=current-work`).
    await git(repoDir, ['update-ref', 'refs/remotes/origin/feat/login', baseSha])

    const out = await getBranchChangedFiles(repoDir, 'feat/login', 'HEAD')
    expect(out.base).toBe('feat/login')
    expect(out.head).toBe('HEAD')
    expect(out.files.map((f) => f.path).sort()).toEqual(['shared.txt'])
  })
})

// ============================================================================
// getBranchFileDiff — branch scope (Phase 3)
// ============================================================================

describe('getBranchFileDiff', () => {
  it('returns unified-diff text between two refs', async () => {
    await realFs.writeFile(path.join(repoDir, 'a.txt'), 'before\n')
    await commit('init')
    await git(repoDir, ['checkout', '-q', '-b', 'feature'])
    await realFs.writeFile(path.join(repoDir, 'a.txt'), 'after\n')
    await commit('feature change')

    const out = await getBranchFileDiff(repoDir, 'main', 'feature', 'a.txt')
    expect(out.isBinary).toBe(false)
    expect(out.isTruncated).toBe(false)
    expect(out.unifiedDiff ?? '').toContain('-before')
    expect(out.unifiedDiff ?? '').toContain('+after')
  })

  it('throws InvalidGitRefError on nonexistent base', async () => {
    await realFs.writeFile(path.join(repoDir, 'a.txt'), 'hi\n')
    await commit('init')

    await expect(
      getBranchFileDiff(repoDir, 'no-such-branch', 'HEAD', 'a.txt'),
    ).rejects.toBeInstanceOf(InvalidGitRefError)
  })

  it('resolves bare "main" via origin/main when no local main exists', async () => {
    // Same partial-clone shape as the getBranchChangedFiles test above —
    // motivated by the GlennCode runtime that only checks out the feature
    // branch but needs the default-branch comparison to work.
    await realFs.writeFile(path.join(repoDir, 'a.txt'), 'main version\n')
    await commit('main: base')
    const mainSha = await readSha(repoDir)

    await git(repoDir, ['checkout', '-q', '-b', 'feature'])
    await realFs.writeFile(path.join(repoDir, 'a.txt'), 'feature version\n')
    await commit('feature: change')

    await git(repoDir, ['branch', '-D', 'main'])
    await git(repoDir, ['update-ref', 'refs/remotes/origin/main', mainSha])

    const out = await getBranchFileDiff(repoDir, 'main', 'HEAD', 'a.txt')
    expect(out.isBinary).toBe(false)
    expect(out.unifiedDiff ?? '').toContain('-main version')
    expect(out.unifiedDiff ?? '').toContain('+feature version')
  })
})

// ============================================================================
// getCommitRange — branch scope (Phase 3)
// ============================================================================

describe('getCommitRange', () => {
  it('returns commits reachable from headRef but not baseRef, newest first', async () => {
    await realFs.writeFile(path.join(repoDir, 'a.txt'), 'a\n')
    await commit('initial main')

    await git(repoDir, ['checkout', '-q', '-b', 'feature'])
    await realFs.writeFile(path.join(repoDir, 'a.txt'), 'a2\n')
    await commit('first feature commit')
    await realFs.writeFile(path.join(repoDir, 'a.txt'), 'a3\n')
    await commit('second feature commit')

    const out = await getCommitRange(repoDir, 'main', 'feature')
    expect(out).toHaveLength(2)
    // git log is newest-first.
    expect(out[0]?.message).toBe('second feature commit')
    expect(out[1]?.message).toBe('first feature commit')
    expect(out[0]?.sha).toMatch(/^[0-9a-f]{40}$/)
    expect(out[0]?.authorName).toBe('Test')
    // ISO-8601 with timezone — "YYYY-MM-DDTHH:MM:SS±HH:MM" or "Z".
    expect(out[0]?.authorDate).toMatch(/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}/)
  })

  it('returns an empty array when there are no commits in the range', async () => {
    await realFs.writeFile(path.join(repoDir, 'a.txt'), 'a\n')
    await commit('only commit')

    const out = await getCommitRange(repoDir, 'HEAD', 'HEAD')
    expect(out).toEqual([])
  })

  it('respects the limit parameter', async () => {
    await realFs.writeFile(path.join(repoDir, 'a.txt'), 'a\n')
    await commit('initial main')

    await git(repoDir, ['checkout', '-q', '-b', 'feature'])
    for (let i = 0; i < 5; i++) {
      await realFs.writeFile(path.join(repoDir, 'a.txt'), `v${i}\n`)
      await commit(`commit ${i}`)
    }

    const out = await getCommitRange(repoDir, 'main', 'feature', 3)
    expect(out).toHaveLength(3)
  })

  it('throws InvalidGitRefError on nonexistent base', async () => {
    await realFs.writeFile(path.join(repoDir, 'a.txt'), 'hi\n')
    await commit('init')

    await expect(getCommitRange(repoDir, 'no-such-ref')).rejects.toBeInstanceOf(
      InvalidGitRefError,
    )
  })

  it('resolves bare "main" via origin/main when no local main exists', async () => {
    // Same partial-clone shape — `main` only as a remote-tracking ref.
    // The commit picker still needs to enumerate "what's on this branch but
    // not on main" so the user can pick a base commit.
    await realFs.writeFile(path.join(repoDir, 'a.txt'), 'v0\n')
    await commit('main: base')
    const mainSha = await readSha(repoDir)

    await git(repoDir, ['checkout', '-q', '-b', 'feature'])
    await realFs.writeFile(path.join(repoDir, 'a.txt'), 'v1\n')
    await commit('feature: first')
    await realFs.writeFile(path.join(repoDir, 'a.txt'), 'v2\n')
    await commit('feature: second')

    await git(repoDir, ['branch', '-D', 'main'])
    await git(repoDir, ['update-ref', 'refs/remotes/origin/main', mainSha])

    const out = await getCommitRange(repoDir, 'main')
    expect(out).toHaveLength(2)
    expect(out[0]?.message).toBe('feature: second')
    expect(out[1]?.message).toBe('feature: first')
  })

  it('handles commit messages with quotes and special chars', async () => {
    await realFs.writeFile(path.join(repoDir, 'a.txt'), 'a\n')
    await commit('init')

    await git(repoDir, ['checkout', '-q', '-b', 'feature'])
    await realFs.writeFile(path.join(repoDir, 'a.txt'), 'b\n')
    // Quotes and backticks in commit messages — tab separator must survive.
    await commit('fix: "quoted" thing & `backtick`')

    const out = await getCommitRange(repoDir, 'main', 'feature')
    expect(out).toHaveLength(1)
    expect(out[0]?.message).toBe('fix: "quoted" thing & `backtick`')
  })
})

// ============================================================================
// looksLikePlainBranchName — predicate that gates the partial-clone fallback
//
// Coverage matrix:
//   - Plain names (with and without nested namespaces) → accepted.
//   - Already-qualified refs (refs/heads/..., refs/remotes/...) → rejected.
//   - Rev-walk / refspec syntax (~, ^, :, ?, *, [, .., @{...}) → rejected.
//   - Structural disasters per git-refname(7) (leading -, ., /; trailing /,
//     .lock, .; embedded //) → rejected.
//
// Locks in the second-iteration predicate that accepts slashed branches
// (feat/login, release/2025-05, dependabot/...). The first iteration
// rejected all of those and silently broke partial-clone diff for users on
// nested namespaces.
// ============================================================================
describe('looksLikePlainBranchName', () => {
  it('accepts flat branch names', () => {
    for (const name of ['main', 'master', 'develop', 'lab', 'v1', 'foo_bar', 'foo-bar', 'foo.bar']) {
      expect(looksLikePlainBranchName(name)).toBe(true)
    }
  })

  it('accepts slashed branch names (the regression this predicate-rev fixed)', () => {
    for (const name of [
      'feat/login',
      'release/2025-05',
      'dependabot/npm/react-19',
      'team/will/scratch',
      'hotfix/v1.2.3',
    ]) {
      expect(looksLikePlainBranchName(name)).toBe(true)
    }
  })

  it('rejects already-qualified refs', () => {
    expect(looksLikePlainBranchName('refs/heads/main')).toBe(false)
    expect(looksLikePlainBranchName('refs/remotes/origin/main')).toBe(false)
    expect(looksLikePlainBranchName('refs/tags/v1')).toBe(false)
  })

  it('rejects rev-walk and refspec syntax', () => {
    for (const expr of ['HEAD', 'HEAD~1', 'main^', 'main^2', 'main..feature', 'main...feature', 'HEAD@{1}']) {
      expect(looksLikePlainBranchName(expr)).toBe(false)
    }
  })

  it('rejects empties and structural disasters', () => {
    for (const bad of ['', '-foo', '.foo', '/foo', 'foo/', 'foo//bar', 'foo.lock', 'foo.', 'foo bar']) {
      expect(looksLikePlainBranchName(bad)).toBe(false)
    }
  })
})

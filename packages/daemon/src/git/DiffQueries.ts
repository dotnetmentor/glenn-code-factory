// DiffQueries — Phase 1 of the diff-view-tab spec.
//
// Pure read-side wrappers around `git status --porcelain=v2`,
// `git diff --numstat HEAD`, and `git diff -U3` for the working-tree scope.
// The functions here are intentionally:
//
//   - Stateless: every call shells out fresh; nothing is cached. Cache lives
//     in the React Query layer on the frontend (per spec section 2.5).
//   - Daemon-only: no SignalR, no audit fan-out, no commit/queue coupling.
//     GitModule.getChangedFiles / getFileDiff wrap these inside the existing
//     serialisation queue so they can't race auto-commit / rebase work.
//   - Thin: parsing each git format is mechanical; the bulk of the file is
//     comments documenting the wire format so a future spec change to git's
//     output is easy to spot.
//
// Why we don't reuse GitRunner here:
//
//   - GitRunner emits `started`/`completed` audit events that land in the
//     `GitOperations` audit table. Diff queries are pure reads — auditing
//     them would inflate the table by 10-100x with no value (the user is
//     just clicking around the Changes tab).
//   - GitRunner injects `GIT_SSH_COMMAND` + `GIT_TERMINAL_PROMPT=0` for
//     network ops; diff queries are local-only and don't need either.
//
// Caps from the spec:
//
//   - Per-file diff body: 500 KB. Above this, return `isTruncated=true`
//     with the head-of-file slice. The frontend prepends a calm warning row.
//   - Total files: 5000. Above this, truncate the array, set
//     `reason="too-many"`. The frontend renders a muted footer.
//   - Binary files: detected via `-` in numstat or `Binary files ... differ`
//     line. Returned with no body, `isBinary=true`, `reason="binary"`.

import { spawn } from 'node:child_process'
import { stat } from 'node:fs/promises'
import path from 'node:path'

const GIT_BINARY = '/usr/bin/git'

/**
 * Per-file diff body cap. Above this we return the head slice and set
 * `isTruncated=true`. 500 KB is generous enough for almost every real diff
 * (the largest commits in the wild rarely top 100 KB) but keeps a single
 * accidental dump from filling the SignalR frame.
 */
export const MAX_DIFF_BODY_BYTES = 500 * 1024

/**
 * Total file cap on `getWorkingTreeChangedFiles`. Above this the array is
 * truncated and `reason="too-many"` is set so the frontend can render a
 * "showing first 5,000 of N" footer. Hard cap because a runaway codegen
 * tool can produce 100k changed files and we don't want to ship that array
 * over the wire.
 */
export const MAX_FILES = 5000

/**
 * Subset of the spec's `ChangedFile` wire shape — kept camelCase locally,
 * the C# binder accepts camelCase against PascalCase records (same
 * convention every other daemon-emitted payload uses).
 */
export interface ChangedFile {
  path: string
  oldPath: string | null
  status:
    | 'added'
    | 'modified'
    | 'deleted'
    | 'renamed'
    | 'untracked'
    | 'binary-modified'
  additions: number
  deletions: number
  isBinary: boolean
  sizeBytes: number | null
}

export interface ChangedFilesResult {
  scope: string
  base: string | null
  head: string | null
  totalAdditions: number
  totalDeletions: number
  files: ChangedFile[]
  reason: string | null
}

export interface FileDiffResult {
  path: string
  status: string
  isBinary: boolean
  isTruncated: boolean
  unifiedDiff: string | null
  reason: string | null
}

/**
 * Single commit summary returned by {@link getCommitRange}. The daemon emits
 * one of these per `baseRef..headRef` commit in newest-first order (git log's
 * default). Author date is ISO-8601 with TZ offset so the frontend can format
 * it locally without a second parse pass.
 */
export interface CommitInfo {
  sha: string
  message: string
  authorDate: string
  authorName: string
}

/**
 * Run a git command from `cwd` and return the captured stdout as a Buffer.
 * Errors are surfaced as rejections; non-zero exit codes are treated as
 * "no output" rather than throwing — git uses non-zero exits for a long tail
 * of "expected" cases (e.g. `git diff` returns 1 when there's a diff,
 * `git diff --no-index` returns 1 when files differ).
 *
 * `acceptableExitCodes` is the small set callers can opt into when the
 * non-zero is documented behaviour. Anything outside the set rejects so
 * callers don't silently miss real failures.
 */
async function runGit(
  cwd: string,
  args: string[],
  options: { acceptableExitCodes?: readonly number[] } = {},
): Promise<Buffer> {
  const acceptable = new Set(options.acceptableExitCodes ?? [0])

  return await new Promise<Buffer>((resolve, reject) => {
    const child = spawn(GIT_BINARY, args, {
      cwd,
      env: {
        ...process.env,
        // No interactive prompts. Git defaults to opening the user's $EDITOR
        // for some commands and asking for credentials over the terminal —
        // both would hang us forever inside the daemon.
        GIT_TERMINAL_PROMPT: '0',
      },
      stdio: ['ignore', 'pipe', 'pipe'],
    })

    const stdoutChunks: Buffer[] = []
    const stderrChunks: Buffer[] = []

    child.stdout.on('data', (chunk: Buffer) => stdoutChunks.push(chunk))
    child.stderr.on('data', (chunk: Buffer) => stderrChunks.push(chunk))

    child.on('error', (err) => reject(err))
    child.on('close', (code) => {
      if (code !== null && acceptable.has(code)) {
        resolve(Buffer.concat(stdoutChunks))
        return
      }
      const stderr = Buffer.concat(stderrChunks).toString('utf8')
      reject(
        new Error(
          `git ${args.join(' ')} exited with code ${String(code)}: ${stderr.slice(0, 500)}`,
        ),
      )
    })
  })
}

/**
 * Parse `git status --porcelain=v2 -z`. The format is documented at
 * https://git-scm.com/docs/git-status#_porcelain_format_version_2. Each
 * record is NUL-terminated. The first character of each record discriminates
 * the kind:
 *
 *   - `1 XY ...` ordinary changed entry
 *   - `2 XY ...` renamed/copied entry — followed by the original path AFTER
 *     a separate NUL inside the same record
 *   - `u XY ...` unmerged (conflict) entry
 *   - `?` untracked
 *   - `!` ignored (we don't request these)
 *
 * For the `2 ` (rename) case, the record itself contains TWO paths separated
 * by an extra NUL byte — so the consumer has to read one extra `\0`-delimited
 * token after seeing the `2` discriminator. The split-by-NUL strategy below
 * is aware of this.
 */
export function parsePorcelainV2(
  raw: string,
): Array<{
  status: ChangedFile['status']
  path: string
  oldPath: string | null
  isSubmodule: boolean
}> {
  const records = raw.split('\0').filter((s) => s.length > 0)
  const out: Array<{
    status: ChangedFile['status']
    path: string
    oldPath: string | null
    isSubmodule: boolean
  }> = []

  let i = 0
  while (i < records.length) {
    const record = records[i] as string
    const kind = record[0]

    if (kind === '?') {
      // Untracked: "? path"
      const filePath = record.slice(2)
      out.push({ status: 'untracked', path: filePath, oldPath: null, isSubmodule: false })
      i += 1
      continue
    }

    if (kind === '!') {
      // Ignored. We never request these (--untracked-files=normal excludes
      // them) but be defensive: skip the record.
      i += 1
      continue
    }

    if (kind === '1') {
      // "1 XY sub <mH> <mI> <mW> <hH> <hI> path"
      // XY is two chars: index status, working-tree status. Path is the rest.
      const parts = record.split(' ')
      // parts[0] = "1", parts[1] = "XY", parts[2] = "sub", ..., parts[8+] = path
      const xy = parts[1] ?? ''
      const sub = parts[2] ?? 'N...'
      // Path may contain spaces — rejoin from index 8 onward.
      const filePath = parts.slice(8).join(' ')
      const status = mapXYToStatus(xy)
      const isSubmodule = sub.startsWith('S')
      out.push({ status, path: filePath, oldPath: null, isSubmodule })
      i += 1
      continue
    }

    if (kind === '2') {
      // "2 XY sub <mH> <mI> <mW> <hH> <hI> <X><score> path"
      // The original path is in the NEXT record (a separate NUL-terminated
      // token). The `path` field on this record is the new path.
      const parts = record.split(' ')
      const xy = parts[1] ?? ''
      const sub = parts[2] ?? 'N...'
      const newPath = parts.slice(9).join(' ')
      const oldPath = (records[i + 1] ?? '') as string
      const isSubmodule = sub.startsWith('S')
      out.push({
        status: 'renamed',
        path: newPath,
        oldPath,
        isSubmodule,
      })
      // Account for the followup record carrying the old path.
      i += 2
      // Defensive: regardless of the XY content, parsePorcelainV2 forces
      // the status to 'renamed' for `2 ` lines. Use the XY hint only to
      // distinguish copy vs rename if a future caller needs it.
      void xy
      continue
    }

    if (kind === 'u') {
      // Unmerged (conflict) — surface as 'modified' for now. Phase 1 doesn't
      // surface a dedicated conflict status; this matches what `git status`
      // shows the user (a conflicted file IS modified in the working tree).
      const parts = record.split(' ')
      const filePath = parts.slice(10).join(' ')
      out.push({
        status: 'modified',
        path: filePath,
        oldPath: null,
        isSubmodule: false,
      })
      i += 1
      continue
    }

    // Unknown record kind — skip rather than throw.
    i += 1
  }

  return out
}

/**
 * Map the porcelain-v2 XY index/working-tree status pair to our coarse
 * vocabulary. The first char is the staged change, the second is the
 * working-tree change. Either being non-`.` means the file is in the diff;
 * we pick whichever is non-`.` (preferring the staged side when both are).
 */
function mapXYToStatus(xy: string): ChangedFile['status'] {
  const x = xy[0] ?? '.'
  const y = xy[1] ?? '.'
  // Prefer the more "interesting" of the two sides. Ordering matters because
  // git can stack states (e.g. "MM" — modified in both index and working tree).
  const c = x !== '.' ? x : y
  switch (c) {
    case 'A':
      return 'added'
    case 'D':
      return 'deleted'
    case 'R':
      return 'renamed'
    case 'C':
      return 'renamed' // copy — surface as renamed for the UI
    case 'M':
      return 'modified'
    case 'T':
      return 'modified' // type changed (e.g. file ↔ symlink)
    case 'U':
      return 'modified' // unmerged
    default:
      return 'modified'
  }
}

/**
 * Parse `git diff --numstat`. Each line is `<additions>\t<deletions>\t<path>`.
 * Binary files show `-\t-\t<path>`. Renamed files show
 * `<additions>\t<deletions>\t{old => new}` or `oldpath\0newpath` in NUL mode.
 * We parse the non-NUL form (caller passes plain --numstat) and only care
 * about the additions / deletions / binary signal — we already get the
 * path / rename info from porcelain v2.
 */
export function parseNumstat(
  raw: string,
): Map<string, { additions: number; deletions: number; isBinary: boolean }> {
  const out = new Map<string, { additions: number; deletions: number; isBinary: boolean }>()
  for (const line of raw.split('\n')) {
    if (line.length === 0) continue
    const parts = line.split('\t')
    if (parts.length < 3) continue
    const a = parts[0] as string
    const d = parts[1] as string
    const p = parts.slice(2).join('\t')
    const isBinary = a === '-' && d === '-'
    out.set(p, {
      additions: isBinary ? 0 : Number.parseInt(a, 10) || 0,
      deletions: isBinary ? 0 : Number.parseInt(d, 10) || 0,
      isBinary,
    })
  }
  return out
}

/**
 * Working-tree changed-files query. Combines:
 *
 *   1. `git status --porcelain=v2 -z --untracked-files=normal` for the
 *      authoritative file list (handles renames, untracked, deleted, types).
 *   2. `git diff --numstat HEAD` for tracked add/delete counts and binary
 *      detection. Untracked files are counted by reading their size on disk
 *      (they're "all additions" in any meaningful sense).
 *
 * Always runs git with `-c diff.renames=true -c core.quotepath=false` so
 * we get rename detection and unicode paths verbatim — a path containing
 * a non-ASCII character (e.g. `é`) returns as-is rather than as the
 * `\303\251` octal escape git defaults to.
 */
export async function getWorkingTreeChangedFiles(
  rootDir: string,
): Promise<ChangedFilesResult> {
  const statusBuf = await runGit(rootDir, [
    '-c',
    'diff.renames=true',
    '-c',
    'core.quotepath=false',
    'status',
    '--porcelain=v2',
    '-z',
    '--untracked-files=normal',
  ])
  const statusEntries = parsePorcelainV2(statusBuf.toString('utf8'))

  // numstat for tracked changes only (HEAD..working-tree). Untracked files
  // don't show up here — they're counted via fs.stat below.
  // Note: --numstat returns exit 0 even when there are no changes, so the
  // default acceptable={0} is correct.
  const numstatBuf = await runGit(rootDir, [
    '-c',
    'diff.renames=true',
    '-c',
    'core.quotepath=false',
    'diff',
    '--numstat',
    'HEAD',
  ])
  const numstatMap = parseNumstat(numstatBuf.toString('utf8'))

  let totalAdditions = 0
  let totalDeletions = 0
  const files: ChangedFile[] = []
  let reason: string | null = null

  for (const entry of statusEntries) {
    if (files.length >= MAX_FILES) {
      reason = 'too-many'
      break
    }

    let additions = 0
    let deletions = 0
    let isBinary = false
    let sizeBytes: number | null = null

    if (entry.isSubmodule) {
      // Submodule — we don't compute +/- counts. Display the path only.
      // Phase 4 of the spec covers the dedicated submodule rendering.
      const file: ChangedFile = {
        path: entry.path,
        oldPath: entry.oldPath,
        status: entry.status,
        additions: 0,
        deletions: 0,
        isBinary: false,
        sizeBytes: null,
      }
      files.push(file)
      continue
    }

    if (entry.status === 'untracked' || entry.status === 'added') {
      // For an untracked file there's no HEAD blob to diff against — count
      // the entire file as added. Best-effort: failure to stat (e.g. the
      // file was deleted between status and stat) sets size=null and adds 0.
      try {
        const fileStat = await stat(path.join(rootDir, entry.path))
        sizeBytes = fileStat.size
        // Heuristic: anything bigger than the per-file diff cap is treated
        // as binary-ish for display (we won't ship the body anyway). For
        // small files we line-count below by re-reading; that's a tiny IO
        // and only happens once per untracked file, not per scroll.
        if (fileStat.size > MAX_DIFF_BODY_BYTES) {
          isBinary = true
        } else {
          // Use numstat against /dev/null to get a real line count.
          // `git diff --no-index` returns exit 1 when files differ — that's
          // the only case for an untracked file (HEAD has nothing) so we
          // accept exit 1 here.
          try {
            const noIndexBuf = await runGit(
              rootDir,
              [
                '-c',
                'diff.renames=true',
                '-c',
                'core.quotepath=false',
                'diff',
                '--no-index',
                '--numstat',
                '/dev/null',
                entry.path,
              ],
              { acceptableExitCodes: [0, 1] },
            )
            const m = parseNumstat(noIndexBuf.toString('utf8'))
            // The path key in --no-index numstat is the second arg verbatim.
            const counts = m.get(entry.path) ?? m.values().next().value
            if (counts) {
              additions = counts.additions
              deletions = counts.deletions
              isBinary = counts.isBinary
            }
          } catch {
            // Fall back to size-as-additions if --no-index fails.
            additions = 0
          }
        }
      } catch {
        // File vanished between status and stat — surface zeros.
      }
    } else if (entry.status === 'deleted') {
      // Tracked → numstat gives the deletion count.
      const counts = numstatMap.get(entry.path)
      if (counts) {
        additions = counts.additions
        deletions = counts.deletions
        isBinary = counts.isBinary
      }
      // No size for deleted (per the spec).
    } else {
      // modified / renamed — numstat keys on the post-rename path.
      const counts = numstatMap.get(entry.path) ?? (entry.oldPath ? numstatMap.get(entry.oldPath) : undefined)
      if (counts) {
        additions = counts.additions
        deletions = counts.deletions
        isBinary = counts.isBinary
      }
      try {
        const fileStat = await stat(path.join(rootDir, entry.path))
        sizeBytes = fileStat.size
      } catch {
        // ignore — sizeBytes stays null.
      }
    }

    totalAdditions += additions
    totalDeletions += deletions

    const file: ChangedFile = {
      path: entry.path,
      oldPath: entry.oldPath,
      status: isBinary && entry.status === 'modified' ? 'binary-modified' : entry.status,
      additions,
      deletions,
      isBinary,
      sizeBytes,
    }
    files.push(file)
  }

  return {
    scope: 'workingTree',
    base: null,
    head: null,
    totalAdditions,
    totalDeletions,
    files,
    reason,
  }
}

/**
 * Working-tree single-file diff. Tracked files use `git diff -U3 HEAD`;
 * untracked files use `git diff -U3 --no-index /dev/null <path>`. Binary
 * detection is via the `Binary files ... differ` line OR an empty body
 * combined with the file having content (the latter handles --no-index's
 * silent treatment of binary files).
 *
 * Body capped at MAX_DIFF_BODY_BYTES; over the cap, the head slice is
 * returned with `isTruncated=true`.
 */
export async function getWorkingTreeFileDiff(
  rootDir: string,
  filePath: string,
): Promise<FileDiffResult> {
  // First, figure out whether the file is tracked. Use `git ls-files` —
  // it's cheap and unambiguous.
  let isTracked = false
  try {
    const lsBuf = await runGit(rootDir, ['ls-files', '--error-unmatch', '--', filePath], {
      acceptableExitCodes: [0, 1],
    })
    isTracked = lsBuf.toString('utf8').trim().length > 0
  } catch {
    isTracked = false
  }

  // Build the git argv. `-c diff.renames=true -c core.quotepath=false`
  // applies to both branches; --no-index for untracked, HEAD for tracked.
  const baseArgs = ['-c', 'diff.renames=true', '-c', 'core.quotepath=false']
  const args = isTracked
    ? [...baseArgs, 'diff', '-U3', 'HEAD', '--', filePath]
    : [...baseArgs, 'diff', '-U3', '--no-index', '/dev/null', filePath]

  // git diff returns exit 1 when there's a diff (always, for --no-index;
  // and any time HEAD differs from the working tree for the tracked path).
  // We accept 0 and 1 as "got the body".
  let buf: Buffer
  try {
    buf = await runGit(rootDir, args, { acceptableExitCodes: [0, 1] })
  } catch {
    // Path doesn't exist, ls-files lied, etc — surface as no-diff.
    return {
      path: filePath,
      status: 'modified',
      isBinary: false,
      isTruncated: false,
      unifiedDiff: '',
      reason: null,
    }
  }

  // Binary detection. If the body contains the `Binary files ... differ`
  // marker before any actual diff hunk, treat as binary and return no body.
  const utf8 = buf.toString('utf8')
  if (
    utf8.includes('Binary files') &&
    utf8.includes('differ') &&
    !utf8.includes('@@')
  ) {
    return {
      path: filePath,
      status: isTracked ? 'modified' : 'untracked',
      isBinary: true,
      isTruncated: false,
      unifiedDiff: null,
      reason: 'binary',
    }
  }

  // Truncate at the byte cap. Do the slice on the BUFFER (bytes) not on
  // the decoded string — a multi-byte character at the boundary would
  // otherwise produce a U+FFFD replacement. We use the buffer's length to
  // decide whether to clip, then re-decode the head slice.
  if (buf.length > MAX_DIFF_BODY_BYTES) {
    const head = buf.subarray(0, MAX_DIFF_BODY_BYTES).toString('utf8')
    return {
      path: filePath,
      status: isTracked ? 'modified' : 'untracked',
      isBinary: false,
      isTruncated: true,
      unifiedDiff: head,
      reason: 'too-large',
    }
  }

  return {
    path: filePath,
    status: isTracked ? 'modified' : 'untracked',
    isBinary: false,
    isTruncated: false,
    unifiedDiff: utf8,
    reason: null,
  }
}

// ============================================================================
// Branch / commit scope queries (Phase 3 of diff-view-tab spec).
//
// These mirror the working-tree variants above but operate on
// `baseRef..headRef` instead of `HEAD` vs the working tree. The harness
// auto-commits every turn, so working-tree is usually empty — the default
// UX is "compare HEAD against main" via these functions. The user can also
// pick any commit on the branch as `baseRef` (Phase 3 commit picker).
//
// Error handling: a missing / invalid ref causes git to exit non-zero with a
// stderr message. `runGit` rejects in that case; the wrappers below catch +
// re-throw a typed error so the controller can surface a 400 with a
// human-readable message instead of leaking git's stderr verbatim.
// ============================================================================

/**
 * Thrown by the branch-scope queries when one of the supplied refs cannot be
 * resolved by `git rev-parse`. Surfaced to the controller as a 400 so the
 * user sees "base ref not found" rather than a daemon-internal 500. The
 * actual stderr from git is preserved in {@link cause} for operator
 * drill-down via the daemon logs.
 */
export class InvalidGitRefError extends Error {
  readonly ref: string
  constructor(ref: string, cause?: unknown) {
    super(`git ref not found: ${ref}`)
    this.name = 'InvalidGitRefError'
    this.ref = ref
    if (cause !== undefined) {
      // Node's Error has no `cause` in the type signature for older targets,
      // but the runtime supports it. Stash on a private-ish field instead.
      ;(this as { cause?: unknown }).cause = cause
    }
  }
}

/**
 * Resolve `ref` to a canonical name that `git rev-parse --verify` accepts,
 * with one transparent fallback: if `ref` is a bare branch name (no slashes,
 * no rev-walk modifiers) and doesn't exist locally, try `origin/<ref>`. This
 * makes the default "Compare against: main" UX work on partial clones where
 * only the current feature branch is checked out and `main` lives only as a
 * remote-tracking ref.
 *
 * The returned name is what every downstream `git diff` / `git log` call
 * should be parameterised with — verifying `main` exists while still passing
 * the bare name to `git diff` is the bug we shipped pre-fix.
 *
 * Throws {@link InvalidGitRefError} when neither the literal ref nor the
 * `origin/<ref>` fallback resolves. The thrown error always quotes the
 * caller's original ref string, not the fallback — so users see the name
 * they actually typed in the error.
 *
 * <b>Note on safety.</b> The fallback only fires for "looks like a bare
 * branch name" inputs (see {@link looksLikePlainBranchName}). Already-
 * qualified refs (`refs/heads/main`) and rev-walk expressions (`HEAD~1`,
 * `main^`, `main..feat`) are filtered out so we never silently rewrite a
 * qualified ref. Full / abbreviated SHAs hit the primary path because they
 * resolve directly. We don't introduce any shell-interpolation surface —
 * the ref name is still passed as an exec argv, not via a shell.
 */
async function resolveRef(cwd: string, ref: string): Promise<string> {
  // Primary path: the ref as-given. Covers HEAD, SHAs (full + abbrev),
  // origin/main, refs/heads/foo, foo~3, tags, etc.
  try {
    await runGit(cwd, ['rev-parse', '--verify', '--quiet', `${ref}^{commit}`], {
      acceptableExitCodes: [0],
    })
    return ref
  } catch (cause) {
    // Only retry as `origin/<ref>` for plain branch-name-shaped inputs.
    // Already-qualified refs (`refs/heads/main`), rev-walk expressions
    // (`HEAD~1`, `main^`), and refspec syntax are filtered out by
    // looksLikePlainBranchName — prefixing `origin/` onto those would
    // corrupt the meaning. `feat/login`-style names with slashes ARE
    // accepted: they're bare branch names, just in a nested namespace.
    if (looksLikePlainBranchName(ref)) {
      const fallback = `origin/${ref}`
      try {
        await runGit(cwd, ['rev-parse', '--verify', '--quiet', `${fallback}^{commit}`], {
          acceptableExitCodes: [0],
        })
        return fallback
      } catch {
        // Fall through to the original error so the message names the ref
        // the user actually requested.
      }
    }
    throw new InvalidGitRefError(ref, cause)
  }
}

/**
 * True when `ref` is the kind of string that, if it doesn't exist locally,
 * is plausibly the bare name of a branch that lives only on `origin`. We
 * accept slashes because real-world branch names regularly contain them
 * (`feat/login`, `release/2025-05`, `dependabot/npm/react-19`,
 * `team/will/scratch`). Without `/` in the allowed set, the partial-clone
 * fallback was silently broken for everyone with non-flat branch namespaces.
 *
 * We reject:
 *  - Empty strings.
 *  - `HEAD` literally (always resolves locally; `origin/HEAD` means the
 *    remote's default-branch pointer, not the same thing).
 *  - Already-qualified refs (`refs/heads/...`, `refs/remotes/...`, etc.) —
 *    prefixing `origin/` onto those corrupts the meaning.
 *  - Strings containing git-refname-disallowed or shell-meta characters
 *    (`~`, `^`, `:`, `?`, `*`, `[`, `\`, whitespace) — these mean rev-walk
 *    or refspec syntax (`HEAD~1`, `main^`, `origin..feature`).
 *  - Strings with structural problems git-refname(7) rejects anyway
 *    (leading `-`/`.`/`/`, trailing `/`/`.lock`/`.`, embedded `..`/`@{`/`//`).
 *
 * Tags whose names look like plain branches (`v1.2.3`) slip through — the
 * lazy fetch attempt then quietly fails, the diff query re-throws the
 * original "ref not found" error. Wasteful but harmless; not worth
 * specialising for given how rarely the tag-as-base path is exercised.
 *
 * Exported so `GitModule` can apply the same predicate before kicking off a
 * lazy `git fetch` for a missing branch — keeps the "is this fetchable?"
 * rule single-sourced.
 */
export function looksLikePlainBranchName(ref: string): boolean {
  if (ref.length === 0) return false
  if (ref === 'HEAD') return false
  if (ref.startsWith('refs/')) return false
  if (/[\s~^:?*[\\]/.test(ref)) return false
  if (ref.startsWith('-') || ref.startsWith('.') || ref.startsWith('/')) return false
  if (ref.endsWith('/') || ref.endsWith('.') || ref.endsWith('.lock')) return false
  if (ref.includes('..') || ref.includes('@{') || ref.includes('//')) return false
  return /^[A-Za-z0-9._\-/]+$/.test(ref)
}

/**
 * Parse `git diff --name-status -z` output. Each record is
 * `<status>\0<path>\0` (or `<status>\0<oldPath>\0<newPath>\0` for renames).
 * Returns a map keyed on the post-rename path so callers can pair it with
 * the numstat map without a second lookup.
 */
function parseNameStatus(raw: string): Map<string, { status: ChangedFile['status']; oldPath: string | null }> {
  const out = new Map<string, { status: ChangedFile['status']; oldPath: string | null }>()
  const tokens = raw.split('\0')
  let i = 0
  while (i < tokens.length) {
    const code = tokens[i] ?? ''
    if (code.length === 0) {
      i += 1
      continue
    }
    const first = code[0]
    if (first === 'R' || first === 'C') {
      // Rename / copy: <code>\0<oldPath>\0<newPath>\0
      const oldPath = tokens[i + 1] ?? ''
      const newPath = tokens[i + 2] ?? ''
      if (newPath.length > 0) {
        out.set(newPath, { status: 'renamed', oldPath })
      }
      i += 3
      continue
    }
    // Plain entry: <code>\0<path>\0
    const filePath = tokens[i + 1] ?? ''
    if (filePath.length > 0) {
      let status: ChangedFile['status']
      switch (first) {
        case 'A':
          status = 'added'
          break
        case 'D':
          status = 'deleted'
          break
        case 'M':
          status = 'modified'
          break
        case 'T':
          status = 'modified'
          break
        default:
          status = 'modified'
      }
      out.set(filePath, { status, oldPath: null })
    }
    i += 2
  }
  return out
}

/**
 * Numstat-style summary of files changed between two refs. Mirrors the wire
 * shape of {@link getWorkingTreeChangedFiles} so the React picker can swap
 * in the response without a shape adapter. `headRef` defaults to `HEAD` so
 * the default-UX call site can pass just `baseRef`.
 *
 * Runs two git commands:
 *   1. `git diff --numstat --find-renames baseRef..headRef` — +/- counts
 *      and binary detection.
 *   2. `git diff --name-status -z --find-renames baseRef..headRef` — status
 *      (A/M/D/R) plus pre-rename path.
 *
 * Throws {@link InvalidGitRefError} when `baseRef` or `headRef` can't be
 * resolved. Other git errors propagate as plain Error from `runGit`.
 */
export async function getBranchChangedFiles(
  rootDir: string,
  baseRef: string,
  headRef: string = 'HEAD',
): Promise<ChangedFilesResult> {
  // Resolve refs up-front so we surface a clean typed error rather than a
  // generic "git diff exited with code 128" downstream, AND so a bare
  // branch name that only exists on `origin` (partial clones) works without
  // the user having to type the qualified name themselves. Downstream
  // commands use the RESOLVED name, not the caller's original, so a
  // `main` → `origin/main` fallback actually drives the diff.
  const resolvedBase = await resolveRef(rootDir, baseRef)
  const resolvedHead = await resolveRef(rootDir, headRef)

  const range = `${resolvedBase}..${resolvedHead}`

  const numstatBuf = await runGit(rootDir, [
    '-c',
    'diff.renames=true',
    '-c',
    'core.quotepath=false',
    'diff',
    '--numstat',
    '--find-renames',
    range,
  ])
  const numstatMap = parseNumstat(numstatBuf.toString('utf8'))

  const nameStatusBuf = await runGit(rootDir, [
    '-c',
    'diff.renames=true',
    '-c',
    'core.quotepath=false',
    'diff',
    '--name-status',
    '-z',
    '--find-renames',
    range,
  ])
  const nameStatusMap = parseNameStatus(nameStatusBuf.toString('utf8'))

  let totalAdditions = 0
  let totalDeletions = 0
  const files: ChangedFile[] = []
  let reason: string | null = null

  // Iterate name-status (the authoritative file list) and zip in numstat
  // counts. Files that appear in numstat but not name-status are skipped —
  // they shouldn't exist (every diff entry has a status) but be defensive
  // rather than emit a row with missing metadata.
  for (const [filePath, meta] of nameStatusMap) {
    if (files.length >= MAX_FILES) {
      reason = 'too-many'
      break
    }

    let additions = 0
    let deletions = 0
    let isBinary = false

    const counts = numstatMap.get(filePath) ?? (meta.oldPath ? numstatMap.get(meta.oldPath) : undefined)
    if (counts) {
      additions = counts.additions
      deletions = counts.deletions
      isBinary = counts.isBinary
    }

    totalAdditions += additions
    totalDeletions += deletions

    const file: ChangedFile = {
      path: filePath,
      oldPath: meta.oldPath,
      status: isBinary && meta.status === 'modified' ? 'binary-modified' : meta.status,
      additions,
      deletions,
      isBinary,
      sizeBytes: null,
    }
    files.push(file)
  }

  return {
    scope: 'branch',
    base: baseRef,
    head: headRef,
    totalAdditions,
    totalDeletions,
    files,
    reason,
  }
}

/**
 * Single-file unified diff between two refs. Same body-clipping / binary
 * detection rules as {@link getWorkingTreeFileDiff}, just driven by
 * `baseRef..headRef` instead of `HEAD` vs working tree.
 *
 * Throws {@link InvalidGitRefError} when either ref can't be resolved.
 */
export async function getBranchFileDiff(
  rootDir: string,
  baseRef: string,
  headRef: string,
  filePath: string,
): Promise<FileDiffResult> {
  // See resolveRef for why we capture and forward the canonical name — a
  // bare `main` on a partial-clone runtime resolves to `origin/main`, and
  // the downstream `git diff` MUST receive the resolved name or it'll fail
  // identically to the validation step.
  const resolvedBase = await resolveRef(rootDir, baseRef)
  const resolvedHead = await resolveRef(rootDir, headRef)

  const range = `${resolvedBase}..${resolvedHead}`
  const args = [
    '-c',
    'diff.renames=true',
    '-c',
    'core.quotepath=false',
    'diff',
    '-U3',
    '--find-renames',
    range,
    '--',
    filePath,
  ]

  // git diff exits 0 with no body if the file is unchanged, or 0 with body
  // when there's a diff. Unlike the working-tree path, `baseRef..headRef`
  // diffs do NOT use exit-code 1 to signal "differs" — that's a --no-index
  // quirk. Default acceptable={0} is correct here.
  let buf: Buffer
  try {
    buf = await runGit(rootDir, args)
  } catch {
    return {
      path: filePath,
      status: 'modified',
      isBinary: false,
      isTruncated: false,
      unifiedDiff: '',
      reason: null,
    }
  }

  const utf8 = buf.toString('utf8')
  if (
    utf8.includes('Binary files') &&
    utf8.includes('differ') &&
    !utf8.includes('@@')
  ) {
    return {
      path: filePath,
      status: 'modified',
      isBinary: true,
      isTruncated: false,
      unifiedDiff: null,
      reason: 'binary',
    }
  }

  if (buf.length > MAX_DIFF_BODY_BYTES) {
    const head = buf.subarray(0, MAX_DIFF_BODY_BYTES).toString('utf8')
    return {
      path: filePath,
      status: 'modified',
      isBinary: false,
      isTruncated: true,
      unifiedDiff: head,
      reason: 'too-large',
    }
  }

  return {
    path: filePath,
    status: 'modified',
    isBinary: false,
    isTruncated: false,
    unifiedDiff: utf8,
    reason: null,
  }
}

/**
 * List the commits reachable from `headRef` but not `baseRef`, newest first.
 * Drives the Phase 3 commit-picker (the user picks any commit on the branch
 * as the base for the diff). Pure read; no side effects.
 *
 * Uses tab-separated `git log --pretty=format` so commit messages with
 * embedded quotes / shell metacharacters / newlines round-trip cleanly. We
 * split on the first three tabs and treat everything after as the message;
 * single-line messages (the common case) are unaffected.
 *
 * `limit` defaults to 200 — large enough for any realistic branch lifetime
 * (we auto-commit every turn, but even 500 turns is rare). Hard cap so a
 * runaway branch can't ship 100k rows over SignalR.
 *
 * Throws {@link InvalidGitRefError} when either ref can't be resolved.
 */
export async function getCommitRange(
  rootDir: string,
  baseRef: string,
  headRef: string = 'HEAD',
  limit: number = 200,
): Promise<CommitInfo[]> {
  // Resolve refs (with origin/* fallback) and use the resolved names in the
  // `git log` invocation below — same reasoning as the diff variants.
  const resolvedBase = await resolveRef(rootDir, baseRef)
  const resolvedHead = await resolveRef(rootDir, headRef)

  const safeLimit = Math.max(1, Math.min(limit, 1000))
  const range = `${resolvedBase}..${resolvedHead}`

  // %H = full sha, %s = subject, %aI = author date ISO-8601 strict,
  // %an = author name. %x09 = literal tab. We pick tab over " | " or similar
  // because tabs are forbidden in git author names and ISO-8601 dates by
  // construction, so the split is unambiguous. Subject CAN contain tabs in
  // theory (a multi-line commit message stripped to its first line); we
  // rejoin everything past index 2 to be safe.
  const buf = await runGit(rootDir, [
    'log',
    `--pretty=format:%H%x09%s%x09%aI%x09%an`,
    `-n`,
    String(safeLimit),
    range,
  ])

  const text = buf.toString('utf8')
  const out: CommitInfo[] = []
  for (const line of text.split('\n')) {
    if (line.length === 0) continue
    const parts = line.split('\t')
    // Minimum well-formed line has 4 tab-separated fields. If git ever
    // emits a malformed row (corrupt repo, etc) we skip rather than throw.
    if (parts.length < 4) continue
    const sha = parts[0] as string
    // Author name is the last field; author date is the second-to-last.
    // Rejoin everything between to recover a subject that might contain a
    // literal tab.
    const authorName = parts[parts.length - 1] as string
    const authorDate = parts[parts.length - 2] as string
    const message = parts.slice(1, parts.length - 2).join('\t')
    out.push({ sha, message, authorDate, authorName })
  }
  return out
}

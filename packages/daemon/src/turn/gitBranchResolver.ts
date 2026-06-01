import { execFile } from 'node:child_process'
import { promisify } from 'node:util'

const execFileAsync = promisify(execFile)

export type GitBranchResolver = () => Promise<string | null>

export function buildCachedGitBranchResolver(opts: {
  cwd: string
  exec?: (
    file: string,
    args: readonly string[],
    options: { cwd: string; timeout: number },
  ) => Promise<{ stdout: string }>
  now?: () => number
  ttlMs?: number
}): GitBranchResolver {
  const exec =
    opts.exec ??
    ((file, args, options) =>
      execFileAsync(file, args as string[], options) as Promise<{ stdout: string }>)
  const now = opts.now ?? Date.now
  const ttlMs = opts.ttlMs ?? 5_000

  let cached: { value: string | null; at: number } | null = null

  return async () => {
    const t = now()
    if (cached !== null && t - cached.at < ttlMs) {
      return cached.value
    }
    let value: string | null = null
    try {
      const { stdout } = await exec(
        'git',
        ['rev-parse', '--abbrev-ref', 'HEAD'],
        { cwd: opts.cwd, timeout: 2_000 },
      )
      const trimmed = stdout.split('\n')[0]?.trim() ?? ''
      if (trimmed.length > 0 && trimmed !== 'HEAD') {
        value = trimmed
      }
    } catch {
      value = null
    }
    cached = { value, at: t }
    return value
  }
}

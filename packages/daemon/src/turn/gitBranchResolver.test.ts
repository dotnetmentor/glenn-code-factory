import { describe, expect, it, vi } from 'vitest'

import { buildCachedGitBranchResolver } from './gitBranchResolver.js'

describe('buildCachedGitBranchResolver', () => {
  it('returns branch name on success and caches within TTL', async () => {
    const exec = vi.fn(async () => ({ stdout: 'main\n' }))
    const now = vi.fn()
    now.mockReturnValueOnce(0).mockReturnValueOnce(1000).mockReturnValueOnce(2000)

    const resolve = buildCachedGitBranchResolver({
      cwd: '/repo',
      exec,
      now,
      ttlMs: 5000,
    })

    expect(await resolve()).toBe('main')
    expect(await resolve()).toBe('main')
    expect(exec).toHaveBeenCalledTimes(1)
  })

  it('returns null on git failure', async () => {
    const exec = vi.fn(async () => {
      throw new Error('git failed')
    })
    const resolve = buildCachedGitBranchResolver({ cwd: '/repo', exec })
    expect(await resolve()).toBeNull()
  })
})

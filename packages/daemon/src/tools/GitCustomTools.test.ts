import { describe, expect, it, vi } from 'vitest'

import type { GitModule } from '../git/GitModule.js'
import type { ToolContext } from '../turn/types.js'
import { buildGitCustomTools } from './GitCustomTools.js'

const CTX: ToolContext = {
  signalr: {} as ToolContext['signalr'],
  config: {} as ToolContext['config'],
  sessionId: 'sess-1',
  turnId: 'turn-1',
}

function makeLogger() {
  return {
    child: () => makeLogger(),
    warn: vi.fn(),
    info: vi.fn(),
    error: vi.fn(),
    debug: vi.fn(),
  } as unknown as import('pino').Logger
}

describe('buildGitCustomTools', () => {
  it('git_status returns unavailable when git module is not wired', async () => {
    const tools = buildGitCustomTools({ logger: makeLogger(), getGitModule: () => undefined })
    const tool = tools.find((t) => t.name === 'git_status')!
    const result = await tool.run({}, CTX)
    expect(result).toMatchObject({ ok: false, error: expect.stringContaining('not available') })
  })

  it('git_sync_with_origin forwards to GitModule.syncWithOrigin', async () => {
    const syncWithOrigin = vi.fn(async () => ({
      ok: true,
      branch: 'main',
      message: 'synced',
    }))
    const git = { syncWithOrigin } as unknown as GitModule
    const tools = buildGitCustomTools({ logger: makeLogger(), getGitModule: () => git })
    const tool = tools.find((t) => t.name === 'git_sync_with_origin')!
    const result = await tool.run({ branch: 'main' }, CTX)
    expect(syncWithOrigin).toHaveBeenCalledWith('main', {
      conversationId: 'sess-1',
      turnId: 'turn-1',
    })
    expect(result).toEqual({ ok: true, branch: 'main', message: 'synced' })
  })

  it('git_start_merge calls mergeLeaveConflicts', async () => {
    const mergeLeaveConflicts = vi.fn(async () => ({
      ok: false,
      conflict: true,
      files: ['x.ts'],
    }))
    const git = { mergeLeaveConflicts } as unknown as GitModule
    const tools = buildGitCustomTools({ logger: makeLogger(), getGitModule: () => git })
    const tool = tools.find((t) => t.name === 'git_start_merge')!
    await tool.run({ branch: 'main' }, CTX)
    expect(mergeLeaveConflicts).toHaveBeenCalledWith('main', {
      conversationId: 'sess-1',
      turnId: 'turn-1',
    })
  })
})

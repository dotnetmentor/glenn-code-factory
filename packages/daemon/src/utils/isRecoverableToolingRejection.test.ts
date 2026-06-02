import { describe, expect, it } from 'vitest'

import {
  isNonFatalOrphanedError,
  isRecoverableToolingRejection,
} from './isRecoverableToolingRejection.js'
import { isBenignAbortRejection } from './isBenignAbortRejection.js'

describe('isRecoverableToolingRejection', () => {
  it('matches spawn ENOENT (production 2026-06-02 glenncodelab crash)', () => {
    const err = Object.assign(new Error('spawn /usr/bin/bash ENOENT'), {
      errno: -2,
      code: 'ENOENT',
      syscall: 'spawn',
      path: '/usr/bin/bash',
    })
    expect(isRecoverableToolingRejection(err)).toBe(true)
  })

  it('matches EPIPE from Cursor SDK BashState stack', () => {
    const err = Object.assign(new Error('write EPIPE'), {
      errno: -32,
      code: 'EPIPE',
      syscall: 'write',
    })
    err.stack = [
      'Error: write EPIPE',
      '    at BashState.execute (file:///opt/agent/node_modules/@cursor/sdk/dist/esm/index.js:8:4866567)',
      '    at LazyTerminalExecutor.execute (file:///opt/agent/node_modules/@cursor/sdk/dist/esm/index.js:8:4869296)',
    ].join('\n')
    expect(isRecoverableToolingRejection(err)).toBe(true)
  })

  it('rejects generic application errors', () => {
    expect(isRecoverableToolingRejection(new Error('something broke'))).toBe(false)
    expect(isRecoverableToolingRejection(new TypeError('cannot read x'))).toBe(false)
  })

  it('rejects ENOENT that is not spawn-related', () => {
    const err = Object.assign(new Error("ENOENT: no such file or directory, open '/data/missing'"), {
      code: 'ENOENT',
      syscall: 'open',
    })
    expect(isRecoverableToolingRejection(err)).toBe(false)
  })
})

describe('isNonFatalOrphanedError', () => {
  it('includes benign abort rejections', () => {
    const err = Object.assign(new Error('[canceled] This operation was aborted'), {
      name: 'ConnectError',
      code: 1,
    })
    expect(isBenignAbortRejection(err)).toBe(true)
    expect(isNonFatalOrphanedError(err)).toBe(true)
  })

  it('includes recoverable tooling rejections', () => {
    const err = Object.assign(new Error('spawn /usr/bin/bash ENOENT'), {
      code: 'ENOENT',
      syscall: 'spawn',
    })
    expect(isNonFatalOrphanedError(err)).toBe(true)
  })
})

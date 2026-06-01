import { describe, expect, it } from 'vitest'
import { IndefiniteReconnectPolicy } from './retryPolicy.js'

function ctx(previousRetryCount: number) {
  return {
    previousRetryCount,
    elapsedMilliseconds: previousRetryCount * 1000,
    retryReason: new Error('test'),
  }
}

describe('IndefiniteReconnectPolicy', () => {
  const policy = new IndefiniteReconnectPolicy()

  it('returns 0ms for the first attempt', () => {
    expect(policy.nextRetryDelayInMilliseconds(ctx(0))).toBe(0)
  })

  it('follows the standard backoff schedule for attempts 1..4', () => {
    expect(policy.nextRetryDelayInMilliseconds(ctx(1))).toBe(2_000)
    expect(policy.nextRetryDelayInMilliseconds(ctx(2))).toBe(5_000)
    expect(policy.nextRetryDelayInMilliseconds(ctx(3))).toBe(10_000)
    expect(policy.nextRetryDelayInMilliseconds(ctx(4))).toBe(30_000)
  })

  it('settles into a fixed 30s cadence from attempt 5 onward', () => {
    expect(policy.nextRetryDelayInMilliseconds(ctx(5))).toBe(30_000)
    expect(policy.nextRetryDelayInMilliseconds(ctx(10))).toBe(30_000)
    expect(policy.nextRetryDelayInMilliseconds(ctx(100))).toBe(30_000)
    expect(policy.nextRetryDelayInMilliseconds(ctx(10_000))).toBe(30_000)
  })

  it('never returns null (i.e. never gives up)', () => {
    for (let i = 0; i < 50; i++) {
      expect(policy.nextRetryDelayInMilliseconds(ctx(i))).not.toBeNull()
    }
  })

  it('treats negative previousRetryCount defensively as 0', () => {
    expect(policy.nextRetryDelayInMilliseconds(ctx(-1))).toBe(0)
    expect(policy.nextRetryDelayInMilliseconds(ctx(-100))).toBe(0)
  })
})

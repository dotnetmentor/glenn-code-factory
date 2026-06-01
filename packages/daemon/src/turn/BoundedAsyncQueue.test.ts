// Tests for BoundedAsyncQueue. The queue is a single-producer, single-consumer
// async FIFO with backpressure — exercised here against the four interesting
// states: empty/full × open/closed, plus the wakeup paths in both directions
// and the iterator's drain-then-end semantics.

import { describe, expect, it } from 'vitest'

import { BoundedAsyncQueue, QueueClosedError } from './BoundedAsyncQueue.js'

/** Drain N items off an iterator into an array. */
async function takeN<T>(iter: AsyncIterator<T>, n: number): Promise<T[]> {
  const out: T[] = []
  for (let i = 0; i < n; i++) {
    const r = await iter.next()
    if (r.done) break
    out.push(r.value)
  }
  return out
}

describe('BoundedAsyncQueue', () => {
  it('rejects non-positive capacity', () => {
    expect(() => new BoundedAsyncQueue<number>(0)).toThrow(RangeError)
    expect(() => new BoundedAsyncQueue<number>(-1)).toThrow(RangeError)
    expect(() => new BoundedAsyncQueue<number>(1.5)).toThrow(RangeError)
  })

  it('push then iterate preserves FIFO order', async () => {
    const q = new BoundedAsyncQueue<number>(10)
    await q.push(1)
    await q.push(2)
    await q.push(3)
    q.close()
    const seen: number[] = []
    for await (const v of q) seen.push(v)
    expect(seen).toEqual([1, 2, 3])
  })

  it('iterate then push wakes the consumer', async () => {
    const q = new BoundedAsyncQueue<number>(10)
    const iter = q[Symbol.asyncIterator]()
    // Consumer parks waiting for data.
    const pending = iter.next()
    // Race: ensure the consumer is parked before we push.
    await Promise.resolve()
    await q.push(42)
    const got = await pending
    expect(got).toEqual({ value: 42, done: false })
  })

  it('push at capacity awaits until consumer drains an item', async () => {
    const q = new BoundedAsyncQueue<number>(2)
    await q.push(1)
    await q.push(2)
    expect(q.size()).toBe(2)

    let resolved = false
    const pushPromise = q.push(3).then(() => {
      resolved = true
    })
    // Yield several microtasks; push must still be parked because we haven't
    // drained anything.
    await Promise.resolve()
    await Promise.resolve()
    expect(resolved).toBe(false)
    expect(q.size()).toBe(2)

    // Consumer drains one — pusher wakes.
    const iter = q[Symbol.asyncIterator]()
    const first = await iter.next()
    expect(first).toEqual({ value: 1, done: false })
    await pushPromise
    expect(resolved).toBe(true)
    expect(q.size()).toBe(2) // 2 and 3 buffered after the wake
  })

  it('close after push lets iterator drain remaining items then end', async () => {
    const q = new BoundedAsyncQueue<string>(5)
    await q.push('a')
    await q.push('b')
    q.close()
    const iter = q[Symbol.asyncIterator]()
    expect(await iter.next()).toEqual({ value: 'a', done: false })
    expect(await iter.next()).toEqual({ value: 'b', done: false })
    const end = await iter.next()
    expect(end.done).toBe(true)
  })

  it('close while consumer is parked ends the iteration', async () => {
    const q = new BoundedAsyncQueue<number>(3)
    const iter = q[Symbol.asyncIterator]()
    const pending = iter.next()
    await Promise.resolve()
    q.close()
    const r = await pending
    expect(r.done).toBe(true)
  })

  it('close while pusher is parked rejects the pending push', async () => {
    const q = new BoundedAsyncQueue<number>(1)
    await q.push(1)
    const pushPromise = q.push(2)
    // Give the pusher a microtask to park on hasSpace.
    await Promise.resolve()
    q.close()
    await expect(pushPromise).rejects.toBeInstanceOf(QueueClosedError)
  })

  it('push after close throws synchronously (as a rejected promise)', async () => {
    const q = new BoundedAsyncQueue<number>(5)
    q.close()
    await expect(q.push(1)).rejects.toBeInstanceOf(QueueClosedError)
  })

  it('close is idempotent', () => {
    const q = new BoundedAsyncQueue<number>(5)
    q.close()
    expect(() => q.close()).not.toThrow()
    expect(q.isClosed()).toBe(true)
  })

  it('bounded high-throughput: producer outpaces consumer, ordering preserved', async () => {
    const q = new BoundedAsyncQueue<number>(4)
    const total = 100
    const produce = (async () => {
      for (let i = 0; i < total; i++) {
        await q.push(i)
      }
      q.close()
    })()

    const consumed: number[] = []
    const consume = (async () => {
      for await (const v of q) {
        consumed.push(v)
        // Simulate a slow consumer by yielding microtasks.
        await Promise.resolve()
      }
    })()

    await Promise.all([produce, consume])
    expect(consumed).toHaveLength(total)
    expect(consumed).toEqual(Array.from({ length: total }, (_, i) => i))
    expect(q.size()).toBe(0)
  })

  it('capacity getter reflects the construction value', () => {
    const q = new BoundedAsyncQueue<number>(7)
    expect(q.capacity).toBe(7)
  })

  it('two-item takeN smoke test', async () => {
    const q = new BoundedAsyncQueue<number>(5)
    await q.push(10)
    await q.push(20)
    q.close()
    const out = await takeN(q[Symbol.asyncIterator](), 5)
    expect(out).toEqual([10, 20])
  })
})

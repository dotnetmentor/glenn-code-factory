// BoundedAsyncQueue — single-producer, single-consumer async FIFO with a hard
// upper bound. Used by TurnRunner to decouple the SDK iterator (`for await`
// over events streamed from the Cursor child process) from the SignalR emit
// pipeline (`await signalr.emitEvent(...)`).
//
// **Why this exists.** Pre-fix, TurnRunner did:
//
//   for await (const event of stream) {
//     // ...
//     await this.#emitSdkEvent(...)   // blocks the loop on every emit
//   }
//
// Every iteration awaited the SignalR invoke synchronously. During heavy
// "scan the repo" turns, the SDK floods us with large tool-result frames and
// the loop ran as slow as the slowest SignalR roundtrip. While the loop was
// pinned awaiting the invoke, the daemon's heartbeat `setInterval` callback
// in HeartbeatModule was starved off the event loop — on a 1-shared-CPU
// machine that's enough to miss the 5s ticker for 18-20s and trip the master's
// heartbeat-watcher into respawning the runtime. (See the class-level remark on
// `HeartbeatWatcherJob` for the post-mortem.)
//
// **What this gives us.** The SDK loop pushes into the queue; a sibling
// consumer task drains it serially and does the emit. Push only awaits when
// the queue is at capacity (true backpressure), which on a healthy run never
// happens. The microtask checkpoints between push/yield let the heartbeat
// timer fire, and the emit pipeline's slowness no longer paces the SDK pull.
//
// **Bound.** A fixed `capacity` keeps memory in check during very large turns
// (otherwise a misbehaving consumer could be outpaced and the queue would grow
// without limit). When full, `push` awaits until the consumer takes an item.
//
// **Concurrency model.** One producer, one consumer. Two pending pushers or
// two pending consumers is undefined behaviour and not used in the codebase.
// Asserted nowhere because the only caller (TurnRunner) is structurally
// single-producer single-consumer per turn.
//
// **Closing semantics.** `close()` is idempotent. After close:
//   - Any items still buffered drain through the consumer in order.
//   - `push()` after close throws — callers must stop pushing first.
//   - A pending `push()` (waiting on backpressure) wakes and throws.
//   - The async iterator returns `done: true` once the buffer drains.

/**
 * Tiny deferred-promise helper. Node 22 has `Promise.withResolvers`, but the
 * daemon's `tsconfig` targets a wider range; the local helper keeps the file
 * self-contained.
 */
function deferred(): { promise: Promise<void>; resolve: () => void } {
  let resolve!: () => void
  const promise = new Promise<void>((r) => {
    resolve = r
  })
  return { promise, resolve }
}

export class QueueClosedError extends Error {
  constructor(message = 'queue is closed') {
    super(message)
    this.name = 'QueueClosedError'
  }
}

export class BoundedAsyncQueue<T> implements AsyncIterable<T> {
  readonly #capacity: number
  readonly #items: T[] = []
  #closed = false
  // Signal resolved when an item becomes available (consumer side).
  #hasData: { promise: Promise<void>; resolve: () => void } | null = null
  // Signal resolved when an item is removed (producer side, only relevant at
  // capacity).
  #hasSpace: { promise: Promise<void>; resolve: () => void } | null = null

  constructor(capacity: number) {
    if (!Number.isInteger(capacity) || capacity < 1) {
      throw new RangeError(`BoundedAsyncQueue capacity must be a positive integer (got ${capacity})`)
    }
    this.#capacity = capacity
  }

  /**
   * Push one item. Resolves immediately if there is space, otherwise waits
   * until the consumer drains an item (or the queue is closed, in which case
   * it throws `QueueClosedError`).
   */
  async push(item: T): Promise<void> {
    if (this.#closed) {
      throw new QueueClosedError('cannot push to closed queue')
    }
    while (this.#items.length >= this.#capacity) {
      this.#hasSpace ??= deferred()
      await this.#hasSpace.promise
      if (this.#closed) {
        throw new QueueClosedError('queue was closed while waiting for space')
      }
    }
    this.#items.push(item)
    // Wake the consumer if it's parked.
    if (this.#hasData !== null) {
      const d = this.#hasData
      this.#hasData = null
      d.resolve()
    }
  }

  /**
   * Mark the queue closed. Buffered items still drain through the consumer.
   * Subsequent `push` calls throw. Idempotent.
   */
  close(): void {
    if (this.#closed) return
    this.#closed = true
    // Unpark both sides so they can observe the closed state.
    if (this.#hasData !== null) {
      const d = this.#hasData
      this.#hasData = null
      d.resolve()
    }
    if (this.#hasSpace !== null) {
      const d = this.#hasSpace
      this.#hasSpace = null
      d.resolve()
    }
  }

  /** Current buffered count. For tests / diagnostics. */
  size(): number {
    return this.#items.length
  }

  /** Whether `close()` has been called. */
  isClosed(): boolean {
    return this.#closed
  }

  /** Configured capacity (max buffered items before push awaits). */
  get capacity(): number {
    return this.#capacity
  }

  async *[Symbol.asyncIterator](): AsyncIterator<T> {
    while (true) {
      if (this.#items.length > 0) {
        const item = this.#items.shift()!
        // Wake one pusher if it was parked on backpressure.
        if (this.#hasSpace !== null) {
          const d = this.#hasSpace
          this.#hasSpace = null
          d.resolve()
        }
        yield item
        continue
      }
      if (this.#closed) {
        return
      }
      this.#hasData ??= deferred()
      await this.#hasData.promise
      // Loop re-checks #items / #closed.
    }
  }
}

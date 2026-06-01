// ProcessStatsCollector — per-process memory / CPU + network rate sampler.
//
// === Spec context (runtime-observability-super-admin B6 + B14) ===
//
// The super-admin runtime drawer wants a small process table (top-by-RSS,
// supervisord-managed only) and a network-bytes-per-second strip without us
// shipping prometheus. Linux already exposes everything we need under /proc
// and /sys/class/net; this collector reads them on a 30s tick and exposes the
// latest snapshot for the heartbeat gather() to attach.
//
// === Why supervisord-managed only ===
//
// `getAllProcessInfo` on the XML-RPC unix socket already gives us the canonical
// list of pids the daemon cares about. Walking /proc ourselves would surface
// every kernel thread and bootstrap binary; we want the user's services. The
// 50-pid cap (top by RSS) is belt-and-braces for an edge case where someone
// has 100+ supervised programs — the React-side table renders at most ~10 rows
// anyway, anything beyond that is sysadmin-noise.
//
// === CPU% derivation ===
//
// `/proc/{pid}/stat` fields 14 (utime) + 15 (stime) are cumulative jiffies the
// process spent in user/kernel mode. The CPU clock tick rate is typically
// 100 Hz on Linux (`getconf CLK_TCK` → 100); we hard-code 100 because reading
// it via posix_sysconf would add a native dep and the value hasn't changed in
// 20+ years on x86_64 Linux. CPU% = (delta_jiffies / interval_ms) * 1000 / 100
// → percent of one core. We multiply by `os.cpus().length` so a process pegging
// every CPU reads ~(100 * #cores)%. First tick has no prior sample → cpuPercent
// reports as 0 (not null — null is the "couldn't read /proc" signal).
//
// === Network ===
//
// `/sys/class/net/{iface}/statistics/rx_bytes` and `tx_bytes` are cumulative
// counters. Per-second rate is `(delta / interval_ms) * 1000`. We prefer eth0
// (matches the runtime container's default) and fall back to the first
// non-`lo` interface if eth0 is missing. The interface name is included in
// the snapshot so operators can spot the fallback.
//
// === Fault model ===
//
// Every /proc read is best-effort: a process can die between the
// getAllProcessInfo call and the /proc read, in which case ENOENT bubbles up
// from `readFileSync`. We catch and skip — the next tick will exclude that
// pid. A missing /sys/class/net interface is treated the same way (network
// fields go null). The collector NEVER throws from its tick loop — failures
// are logged at debug and the snapshot stays whatever the previous tick saw.

import { readFile, readdir } from 'node:fs/promises'
import os from 'node:os'
import type { Logger } from 'pino'

import type { SupervisordXmlRpcClient } from '../supervisord/SupervisordXmlRpcClient.js'

/** Per-process snapshot row. */
export interface ProcessStats {
  name: string
  pid: number
  rssBytes: number
  vmSizeBytes: number
  cpuPercent: number
}

/** Network snapshot — single interface; rates are computed across ticks. */
export interface NetworkStats {
  /** Interface name we sampled (eth0, or first non-lo fallback). */
  interface: string
  /** Cumulative rx bytes from the kernel counter. */
  rxBytes: number
  /** Cumulative tx bytes. */
  txBytes: number
  /** Bytes per second since the previous tick (0 on first tick). */
  rxBytesPerSec: number
  /** Bytes per second since the previous tick (0 on first tick). */
  txBytesPerSec: number
}

/** Full sample emitted on every tick. */
export interface SysstatsSnapshot {
  /** ISO-8601 daemon clock at sample time. */
  sampledAt: string
  /** Up to 50 entries (top by RSS). */
  processes: ProcessStats[]
  /** Null when no interface could be read (no eth0, no non-lo fallback). */
  network: NetworkStats | null
}

export interface ProcessStatsCollectorOptions {
  supervisord: SupervisordXmlRpcClient
  logger: Logger
  /** Sample interval. Default 30_000 (30s). Tests pass smaller values. */
  intervalMs?: number
  /** Top-N processes to keep (by RSS). Default 50. */
  topN?: number
  /** Override /proc root for tests. Default `/proc`. */
  procRoot?: string
  /** Override /sys/class/net root for tests. Default `/sys/class/net`. */
  netRoot?: string
  /**
   * Override the preferred interface. Default `eth0`. Falls back to the first
   * non-`lo` directory under `netRoot` when this is missing.
   */
  preferredInterface?: string
  /** Monotonic clock for delta accounting. Default `Date.now`. */
  now?: () => number
}

const DEFAULT_INTERVAL_MS = 30_000
const DEFAULT_TOP_N = 50
const DEFAULT_PROC_ROOT = '/proc'
const DEFAULT_NET_ROOT = '/sys/class/net'
const DEFAULT_PREFERRED_INTERFACE = 'eth0'
// Linux jiffies-per-second. `getconf CLK_TCK` → 100 on every x86_64 distro
// shipped this decade. Reading sysconf would require a native dep we don't
// otherwise need; the constant is good enough for the per-process CPU%
// surface (which is itself a coarse approximation).
const CLOCK_TICKS_PER_SEC = 100

/**
 * Lightweight per-process + per-interface stats sampler. Runs on a fixed
 * interval; expose the latest sample via {@link ProcessStatsCollector.latest}
 * so the heartbeat gather() can attach it without coordinating ticks.
 */
export class ProcessStatsCollector {
  readonly #supervisord: SupervisordXmlRpcClient
  readonly #logger: Logger
  readonly #intervalMs: number
  readonly #topN: number
  readonly #procRoot: string
  readonly #netRoot: string
  readonly #preferredInterface: string
  readonly #now: () => number
  #timer: NodeJS.Timeout | null = null
  #latest: SysstatsSnapshot | null = null

  /** pid → cumulative ticks last sampled. Used to compute delta CPU. */
  #prevCpuTicks: Map<number, number> = new Map()
  /** Monotonic ms of the previous tick — for the CPU delta + network rate. */
  #prevSampleAtMs: number | null = null
  /** Cumulative rx/tx bytes from the previous tick. Null on first tick. */
  #prevRxBytes: number | null = null
  #prevTxBytes: number | null = null

  constructor(opts: ProcessStatsCollectorOptions) {
    this.#supervisord = opts.supervisord
    this.#logger = opts.logger.child({ module: 'process-stats-collector' })
    this.#intervalMs = opts.intervalMs ?? DEFAULT_INTERVAL_MS
    this.#topN = opts.topN ?? DEFAULT_TOP_N
    this.#procRoot = opts.procRoot ?? DEFAULT_PROC_ROOT
    this.#netRoot = opts.netRoot ?? DEFAULT_NET_ROOT
    this.#preferredInterface = opts.preferredInterface ?? DEFAULT_PREFERRED_INTERFACE
    this.#now = opts.now ?? Date.now

    if (this.#intervalMs < 1_000) {
      throw new RangeError('intervalMs must be >= 1000')
    }
    if (this.#topN < 1) {
      throw new RangeError('topN must be >= 1')
    }
  }

  start(): void {
    if (this.#timer) return // idempotent
    // Fire once immediately so the heartbeat has something to attach on the
    // first beat rather than waiting a full interval.
    void this.#tick()
    this.#timer = setInterval(() => void this.#tick(), this.#intervalMs)
    this.#timer.unref?.()
  }

  stop(): void {
    if (!this.#timer) return // idempotent
    clearInterval(this.#timer)
    this.#timer = null
  }

  /** Latest snapshot, or null until the first tick lands. */
  latest(): SysstatsSnapshot | null {
    return this.#latest
  }

  async #tick(): Promise<void> {
    const tickStartMs = this.#now()
    try {
      const supervisedPids = await this.#listSupervisedPids()
      const processes = await this.#sampleProcesses(supervisedPids, tickStartMs)
      const network = await this.#sampleNetwork(tickStartMs)
      this.#latest = {
        sampledAt: new Date().toISOString(),
        processes,
        network,
      }
      this.#prevSampleAtMs = tickStartMs
    } catch (err) {
      // The tick loop must never crash. Individual sample paths catch their
      // own errors; this is the defensive backstop for an unexpected
      // exception (e.g. XML-RPC client misbehaving).
      this.#logger.debug({ err }, 'tick failed; will retry on next interval')
    }
  }

  async #listSupervisedPids(): Promise<{ name: string; pid: number }[]> {
    try {
      const all = await this.#supervisord.getAllProcessInfo()
      // Only RUNNING-with-a-real-pid entries. supervisord returns 0 for
      // never-started or fully-stopped programs, and reading /proc/0 makes
      // no sense.
      return all
        .filter((p) => p.pid > 0)
        .map((p) => ({ name: p.name, pid: p.pid }))
    } catch (err) {
      // supervisord socket missing / transient blip. Skip this tick's
      // process table; we still try to sample network below.
      this.#logger.debug({ err }, 'getAllProcessInfo failed; skipping process table this tick')
      return []
    }
  }

  async #sampleProcesses(
    pids: { name: string; pid: number }[],
    tickStartMs: number,
  ): Promise<ProcessStats[]> {
    if (pids.length === 0) return []

    const cpuCount = os.cpus().length || 1
    const intervalMs =
      this.#prevSampleAtMs === null ? this.#intervalMs : tickStartMs - this.#prevSampleAtMs
    const newCpuTicks = new Map<number, number>()
    const out: ProcessStats[] = []

    for (const { name, pid } of pids) {
      let status: string
      let stat: string
      try {
        ;[status, stat] = await Promise.all([
          readFile(`${this.#procRoot}/${pid}/status`, 'utf8'),
          readFile(`${this.#procRoot}/${pid}/stat`, 'utf8'),
        ])
      } catch {
        // Process exited between getAllProcessInfo and the /proc read. Skip
        // quietly; supervisord will report a fresh pid next tick.
        continue
      }

      const rssBytes = parseVmKb(status, 'VmRSS') * 1024
      const vmSizeBytes = parseVmKb(status, 'VmSize') * 1024
      const ticks = parseCpuTicks(stat)
      newCpuTicks.set(pid, ticks)

      let cpuPercent = 0
      const prev = this.#prevCpuTicks.get(pid)
      if (prev !== undefined && intervalMs > 0) {
        const delta = Math.max(0, ticks - prev)
        // ticks → seconds: delta / CLOCK_TICKS_PER_SEC.
        // seconds → fraction of interval: (delta / 100) / (intervalMs/1000)
        //                                = (delta * 1000) / (100 * intervalMs)
        // × 100 for % → (delta * 100_000) / (100 * intervalMs)
        //              = (delta * 1000) / intervalMs
        // × cpuCount so a fully-pegged process reads ~100 × #cores.
        cpuPercent = ((delta * 1000) / intervalMs) * cpuCount
      }

      out.push({
        name,
        pid,
        rssBytes,
        vmSizeBytes,
        cpuPercent,
      })
    }

    this.#prevCpuTicks = newCpuTicks

    // Top-N by RSS. Stable secondary sort by name so the rendered table
    // doesn't flicker when two processes have identical RSS.
    out.sort((a, b) => {
      if (b.rssBytes !== a.rssBytes) return b.rssBytes - a.rssBytes
      return a.name.localeCompare(b.name)
    })
    return out.slice(0, this.#topN)
  }

  async #sampleNetwork(tickStartMs: number): Promise<NetworkStats | null> {
    const iface = await this.#resolveInterface()
    if (iface === null) {
      // Reset prev counters so a later-appearing interface doesn't compute a
      // huge artificial delta against stale numbers.
      this.#prevRxBytes = null
      this.#prevTxBytes = null
      return null
    }

    let rxBytes: number
    let txBytes: number
    try {
      const [rxRaw, txRaw] = await Promise.all([
        readFile(`${this.#netRoot}/${iface}/statistics/rx_bytes`, 'utf8'),
        readFile(`${this.#netRoot}/${iface}/statistics/tx_bytes`, 'utf8'),
      ])
      rxBytes = Number.parseInt(rxRaw.trim(), 10)
      txBytes = Number.parseInt(txRaw.trim(), 10)
      if (!Number.isFinite(rxBytes) || !Number.isFinite(txBytes)) {
        return null
      }
    } catch (err) {
      this.#logger.debug({ err, iface }, 'failed to read network counters; skipping this tick')
      return null
    }

    let rxBytesPerSec = 0
    let txBytesPerSec = 0
    const intervalMs =
      this.#prevSampleAtMs === null ? this.#intervalMs : tickStartMs - this.#prevSampleAtMs
    if (
      this.#prevRxBytes !== null &&
      this.#prevTxBytes !== null &&
      intervalMs > 0
    ) {
      rxBytesPerSec = ((rxBytes - this.#prevRxBytes) * 1000) / intervalMs
      txBytesPerSec = ((txBytes - this.#prevTxBytes) * 1000) / intervalMs
      // Counter wrap or interface reset can produce negatives; floor at 0.
      if (rxBytesPerSec < 0) rxBytesPerSec = 0
      if (txBytesPerSec < 0) txBytesPerSec = 0
    }
    this.#prevRxBytes = rxBytes
    this.#prevTxBytes = txBytes

    return {
      interface: iface,
      rxBytes,
      txBytes,
      rxBytesPerSec,
      txBytesPerSec,
    }
  }

  /**
   * Return the preferred interface name if it exists, else the first
   * non-`lo` directory under `/sys/class/net`, else null. We re-check each
   * tick so a late-appearing eth0 (e.g. cloudflared brought it up) is
   * eventually picked up.
   */
  async #resolveInterface(): Promise<string | null> {
    try {
      const entries = await readdir(this.#netRoot)
      if (entries.includes(this.#preferredInterface)) {
        return this.#preferredInterface
      }
      const fallback = entries.find((e) => e !== 'lo')
      return fallback ?? null
    } catch (err) {
      this.#logger.debug({ err, netRoot: this.#netRoot }, 'failed to read net root')
      return null
    }
  }
}

/**
 * Parse the `VmRSS:` / `VmSize:` line from `/proc/{pid}/status`. The format is
 * `VmRSS:   12345 kB` — we trust the kB unit (it has always been kB on every
 * Linux since 2.4) and return the integer. Returns 0 on missing key or
 * malformed line.
 */
function parseVmKb(status: string, key: string): number {
  const lines = status.split('\n')
  for (const line of lines) {
    if (!line.startsWith(key + ':')) continue
    const match = line.match(/(\d+)/)
    if (match === null) return 0
    const n = Number.parseInt(match[1] ?? '0', 10)
    return Number.isFinite(n) ? n : 0
  }
  return 0
}

/**
 * Parse fields 14 (utime) + 15 (stime) from `/proc/{pid}/stat`. The file shape
 * is well-defined but field 2 (comm) is the process name in parens and can
 * contain spaces — we anchor on the closing paren to skip past it cleanly.
 * Returns 0 on malformed input.
 */
function parseCpuTicks(stat: string): number {
  const close = stat.lastIndexOf(')')
  if (close < 0) return 0
  // After the `) `: state, ppid, pgrp, session, tty, tpgid, flags, minflt,
  // cminflt, majflt, cmajflt, utime, stime. utime is field index 11 in the
  // remainder (zero-indexed); stime is index 12.
  const rest = stat.slice(close + 2).split(' ')
  const utime = Number.parseInt(rest[11] ?? '0', 10)
  const stime = Number.parseInt(rest[12] ?? '0', 10)
  if (!Number.isFinite(utime) || !Number.isFinite(stime)) return 0
  return utime + stime
}

/** Exported so consumers can do an `_unused = CLOCK_TICKS_PER_SEC` for the comment. */
export const CLOCK_TICKS_PER_SECOND = CLOCK_TICKS_PER_SEC

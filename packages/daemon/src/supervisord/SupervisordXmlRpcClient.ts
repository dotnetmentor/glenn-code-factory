// SupervisordXmlRpcClient — tiny XML-RPC client over a unix socket targeting
// supervisord's `getAllProcessInfo` endpoint.
//
// === Why a hand-rolled client ===
//
// Supervisord exposes an XML-RPC interface on a unix socket
// (`/var/run/supervisor.sock` by convention; the runtime base image's
// `supervisord.conf` ships with `unix_http_server` enabled at that path). The
// only call we actually need is `supervisor.getAllProcessInfo`, which returns
// an array of structs with the per-process fields the observability spec
// needs (state, pid, exit status, spawn error, log file paths, …).
//
// Pulling a full XML-RPC dependency in for one call is overkill — and the
// daemon ships as a single esbuild bundle, so every npm package added here
// has to survive bundling intact. We hand-roll a 50-ish line client:
//
//   1. Build the XML-RPC request envelope (one method, zero parameters).
//   2. POST it over a unix socket via Node's `http` module (which accepts
//      `socketPath` instead of host/port).
//   3. Parse the response envelope into a `ProcessInfo[]`.
//
// The parser is deliberately minimal — it recognises only the shapes
// supervisord actually emits: `<methodResponse>`, `<array>`, `<struct>`,
// `<value>`, and the scalar types `<string>`, `<int>`, `<i4>`, `<boolean>`,
// `<double>`. Tags we don't recognise produce a `null` value rather than
// throwing, so a future supervisord version that adds a field type we haven't
// seen doesn't crash the poll loop.
//
// === Fault model ===
//
// `getAllProcessInfo` is best-effort observability. A connect failure
// (socket missing, supervisord not running yet) throws — the caller
// (`ServiceStatusPoller`) catches and treats as "no info this tick"; the
// next tick will retry. We deliberately do NOT add retries inside this
// module: a slow supervisord blocking for seconds would chain across ticks
// and the poller's own re-entrancy guard already covers the in-flight case.
//
// === Type fidelity ===
//
// XML-RPC has no native null, so optional fields supervisord might omit
// (e.g. `stop` while the process has never stopped) come through as `0` /
// empty string. We expose the raw decoded values; the consumer interprets
// them. `state` is XML-RPC's int state code; `statename` is the human
// string ("RUNNING", "FATAL", …) — both are present in the response and
// the poller uses `statename` so it can compare against the same vocabulary
// the existing `supervisorctl status` text-parse used.

import { request as httpRequest } from 'node:http'
import type { Logger } from 'pino'

const DEFAULT_SOCKET_PATH = '/var/run/supervisor.sock'
const DEFAULT_TIMEOUT_MS = 5_000
const METHOD_GET_ALL_PROCESS_INFO = 'supervisor.getAllProcessInfo'

/**
 * Subset of supervisord's per-process struct we surface. Field names match the
 * supervisord XML-RPC docs exactly so a reader cross-referencing
 * <http://supervisord.org/api.html> has a one-to-one mapping.
 */
export interface SupervisordProcessInfo {
  name: string
  /** Group name (usually equal to `name` for our flat-program layout). */
  group: string
  /** Numeric state code (e.g. 20 = RUNNING, 200 = FATAL). */
  state: number
  /** Human state name — RUNNING / STARTING / STOPPED / BACKOFF / EXITED / FATAL / UNKNOWN. */
  statename: string
  /** Epoch seconds the process most recently started, or 0 if never. */
  start: number
  /** Server clock at sample time (epoch seconds). */
  now: number
  /** Epoch seconds the process most recently stopped, or 0 if never. */
  stop: number
  /** Last process exit status (signed; 0 when never exited or running). */
  exitstatus: number
  /** Spawn error string supervisord captured the last time start failed. */
  spawnerr: string
  pid: number
  /** Absolute path to supervisord's stdout capture for the process. */
  stdout_logfile: string
  /** Absolute path to supervisord's stderr capture for the process. */
  stderr_logfile: string
  /** Free-form description supervisord renders in `supervisorctl status`. */
  description: string
}

export interface SupervisordXmlRpcClientDeps {
  logger: Logger
  /** Override socket path. Default `/var/run/supervisor.sock`. Tests inject a fake transport. */
  socketPath?: string
  /** Override the per-request timeout in ms. Default 5s. */
  timeoutMs?: number
  /**
   * Inject the HTTP-over-unix-socket transport. Production uses node:http's
   * `request` with `socketPath`. Tests inject a stub that resolves with a
   * pre-built XML-RPC response body without touching the filesystem.
   */
  transport?: SupervisordTransport
}

/**
 * Transport layer the client uses to ship an XML-RPC POST. Factored out so
 * tests can replace it with a stub that returns canned XML — there is no
 * filesystem or networking involved in test execution.
 */
export type SupervisordTransport = (
  socketPath: string,
  body: string,
  timeoutMs: number,
) => Promise<string>

const defaultTransport: SupervisordTransport = (socketPath, body, timeoutMs) =>
  new Promise<string>((resolve, reject) => {
    const req = httpRequest(
      {
        socketPath,
        method: 'POST',
        path: '/RPC2',
        headers: {
          'Content-Type': 'text/xml',
          'Content-Length': Buffer.byteLength(body, 'utf8'),
        },
        timeout: timeoutMs,
      },
      (res) => {
        const chunks: Buffer[] = []
        res.on('data', (chunk: Buffer) => chunks.push(chunk))
        res.on('end', () => {
          const text = Buffer.concat(chunks).toString('utf8')
          if (res.statusCode !== undefined && res.statusCode >= 400) {
            reject(new Error(`supervisord xml-rpc HTTP ${res.statusCode}: ${text.slice(0, 200)}`))
            return
          }
          resolve(text)
        })
        res.on('error', (err) => reject(err))
      },
    )
    req.on('error', (err) => reject(err))
    req.on('timeout', () => {
      req.destroy(new Error(`supervisord xml-rpc timed out after ${timeoutMs}ms`))
    })
    req.write(body, 'utf8')
    req.end()
  })

export class SupervisordXmlRpcClient {
  readonly #logger: Logger
  readonly #socketPath: string
  readonly #timeoutMs: number
  readonly #transport: SupervisordTransport

  constructor(deps: SupervisordXmlRpcClientDeps) {
    this.#logger = deps.logger.child({ module: 'supervisord-xml-rpc' })
    this.#socketPath = deps.socketPath ?? DEFAULT_SOCKET_PATH
    this.#timeoutMs = deps.timeoutMs ?? DEFAULT_TIMEOUT_MS
    this.#transport = deps.transport ?? defaultTransport
  }

  /**
   * Issue `supervisor.getAllProcessInfo`. Returns the decoded array of
   * per-process structs. Rejects on transport / parse errors; the caller is
   * expected to swallow and retry next tick.
   */
  async getAllProcessInfo(): Promise<SupervisordProcessInfo[]> {
    const body =
      '<?xml version="1.0"?>' +
      `<methodCall><methodName>${METHOD_GET_ALL_PROCESS_INFO}</methodName><params/></methodCall>`
    const response = await this.#transport(this.#socketPath, body, this.#timeoutMs)
    const decoded = parseMethodResponse(response)
    if (!Array.isArray(decoded)) {
      this.#logger.warn(
        { decodedType: typeof decoded },
        'supervisord getAllProcessInfo returned a non-array',
      )
      return []
    }
    return decoded.map((entry) => normaliseProcessInfo(entry))
  }
}

// ============================================================================
// Minimal XML-RPC response parser. Recognises only the tag set supervisord
// emits — anything unrecognised resolves to `null` so the poll loop survives
// future supervisord additions.
// ============================================================================

/**
 * Strip XML attributes from a tag name token (e.g. `<int>` or `<int/>` →
 * `int`, `<value>` → `value`). We tolerate self-closing tags because empty
 * `<string/>` does appear in supervisord output (spawnerr when there's no
 * error to report).
 */
function tagName(token: string): string {
  // token is the text inside `<...>` — strip a leading `/` and any
  // whitespace-prefixed attributes.
  let s = token.trim()
  if (s.startsWith('/')) s = s.slice(1)
  const slash = s.indexOf('/')
  const space = s.indexOf(' ')
  let cut = s.length
  if (slash >= 0) cut = Math.min(cut, slash)
  if (space >= 0) cut = Math.min(cut, space)
  return s.slice(0, cut)
}

/** Decode XML entities relevant to supervisord's output. */
function decodeEntities(s: string): string {
  return s
    .replace(/&lt;/g, '<')
    .replace(/&gt;/g, '>')
    .replace(/&quot;/g, '"')
    .replace(/&apos;/g, "'")
    .replace(/&amp;/g, '&')
}

type XmlRpcValue = string | number | boolean | null | XmlRpcValue[] | { [k: string]: XmlRpcValue }

/**
 * Walk-style parser. We keep a position cursor through the source and a value
 * stack for nested `<array>` / `<struct>` / `<value>` constructs. The
 * implementation is deliberately untyped-loose internally — we trust ourselves
 * to keep the stack invariant, and the public surface (`parseMethodResponse`)
 * wraps the unsafe internals.
 */
export function parseMethodResponse(xml: string): XmlRpcValue {
  // Handle XML-RPC fault responses by surfacing the fault as a thrown Error so
  // the caller's promise rejects — same shape any other transport failure
  // takes.
  if (xml.includes('<fault>')) {
    const message = extractFaultMessage(xml)
    throw new Error(`supervisord xml-rpc fault: ${message}`)
  }

  // Strip the XML header / processing instructions so the tag walker doesn't
  // have to special-case them. Supervisord's responses always start with
  // `<?xml ... ?>` then `<methodResponse>`.
  let i = xml.indexOf('<methodResponse>')
  if (i < 0) {
    throw new Error('supervisord xml-rpc: missing <methodResponse>')
  }
  // Position just after `<methodResponse>`. The first nested element is
  // `<params><param><value>...</value></param></params>`.
  i += '<methodResponse>'.length
  const parsed = parseFirstValue(xml, i)
  return parsed.value
}

function extractFaultMessage(xml: string): string {
  // Best-effort: pull the first `faultString` value we see. Supervisord
  // wraps fault strings as `<value><string>...</string></value>`.
  const match = /<name>faultString<\/name>\s*<value>\s*(?:<string>)?([^<]*)/i.exec(xml)
  return match?.[1]?.trim() ?? 'unknown'
}

/**
 * Read forward from `start`, return the first `<value>` block's parsed value
 * and the cursor position after the closing `</value>`. Other tags before the
 * first `<value>` (`<params>`, `<param>`) are skipped.
 */
function parseFirstValue(xml: string, start: number): { value: XmlRpcValue; next: number } {
  let i = start
  while (i < xml.length) {
    const open = xml.indexOf('<', i)
    if (open < 0) break
    const close = xml.indexOf('>', open)
    if (close < 0) break
    const name = tagName(xml.slice(open + 1, close))
    if (name === 'value') {
      const { value, next } = parseValueBody(xml, close + 1)
      return { value, next }
    }
    i = close + 1
  }
  throw new Error('supervisord xml-rpc: no <value> in response')
}

/**
 * Parse the body of a `<value>...</value>` block. `start` points to the first
 * character after `<value>`. Returns the value and the cursor just after
 * `</value>`.
 *
 * XML-RPC says a `<value>` with no type tag is implicitly a string. We honour
 * that: if the first non-whitespace character is not `<`, we treat it as a
 * string literal up to the closing `</value>`.
 */
function parseValueBody(xml: string, start: number): { value: XmlRpcValue; next: number } {
  // Skip leading whitespace.
  let i = start
  while (i < xml.length && /\s/.test(xml[i] ?? '')) i += 1

  if (xml[i] !== '<') {
    // Implicit string (no type tag).
    const closeValue = xml.indexOf('</value>', i)
    if (closeValue < 0) throw new Error('supervisord xml-rpc: unterminated implicit-string <value>')
    const text = decodeEntities(xml.slice(i, closeValue))
    return { value: text, next: closeValue + '</value>'.length }
  }

  // Read the opening type tag.
  const openClose = xml.indexOf('>', i)
  if (openClose < 0) throw new Error('supervisord xml-rpc: malformed type tag')
  const raw = xml.slice(i + 1, openClose)
  const isSelfClose = raw.endsWith('/')
  const name = tagName(raw)
  // Self-closing scalar (e.g. `<string/>`) → empty value of that kind.
  if (isSelfClose) {
    // Skip past `</value>`.
    const closeValue = xml.indexOf('</value>', openClose + 1)
    if (closeValue < 0) throw new Error('supervisord xml-rpc: missing </value> after self-close')
    return {
      value: emptyValueForType(name),
      next: closeValue + '</value>'.length,
    }
  }

  // Has body content. Find the matching close tag.
  const bodyStart = openClose + 1
  let parsed: XmlRpcValue
  let bodyEnd: number

  switch (name) {
    case 'string':
    case 'i4':
    case 'int':
    case 'i8':
    case 'double':
    case 'boolean':
    case 'base64':
    case 'dateTime.iso8601': {
      const closeTag = `</${name}>`
      bodyEnd = xml.indexOf(closeTag, bodyStart)
      if (bodyEnd < 0) throw new Error(`supervisord xml-rpc: missing ${closeTag}`)
      const text = decodeEntities(xml.slice(bodyStart, bodyEnd))
      parsed = coerceScalar(name, text)
      bodyEnd += closeTag.length
      break
    }
    case 'nil': {
      const closeTag = `</nil>`
      bodyEnd = xml.indexOf(closeTag, bodyStart)
      if (bodyEnd < 0) {
        // supervisord doesn't emit <nil> as far as we know; tolerate either
        // form. The tag has no body for the supported XML-RPC extension.
        bodyEnd = bodyStart
      } else {
        bodyEnd += closeTag.length
      }
      parsed = null
      break
    }
    case 'array': {
      const { value, next } = parseArrayBody(xml, bodyStart)
      parsed = value
      bodyEnd = next
      break
    }
    case 'struct': {
      const { value, next } = parseStructBody(xml, bodyStart)
      parsed = value
      bodyEnd = next
      break
    }
    default: {
      // Unknown tag — skip to the matching close and report as null. Keeps
      // the parser forwards-compatible with supervisord versions that
      // sprinkle in new types.
      const closeTag = `</${name}>`
      bodyEnd = xml.indexOf(closeTag, bodyStart)
      if (bodyEnd < 0) throw new Error(`supervisord xml-rpc: unknown tag ${name} without close`)
      bodyEnd += closeTag.length
      parsed = null
    }
  }

  // Consume the closing `</value>`.
  const closeValue = xml.indexOf('</value>', bodyEnd)
  if (closeValue < 0) throw new Error('supervisord xml-rpc: missing </value>')
  return { value: parsed, next: closeValue + '</value>'.length }
}

function parseArrayBody(xml: string, start: number): { value: XmlRpcValue[]; next: number } {
  // <array><data><value>...</value>...<value>...</value></data></array>
  const dataOpen = xml.indexOf('<data>', start)
  const arrayClose = xml.indexOf('</array>', start)
  if (dataOpen < 0 || dataOpen > arrayClose) {
    // Empty array (`<array></array>` or `<array><data/></array>`).
    return { value: [], next: arrayClose + '</array>'.length }
  }
  let i = dataOpen + '<data>'.length
  const out: XmlRpcValue[] = []
  while (true) {
    // Find next `<value>` before `</data>`.
    const nextValue = xml.indexOf('<value>', i)
    const dataClose = xml.indexOf('</data>', i)
    if (nextValue < 0 || nextValue > dataClose) break
    const { value, next } = parseValueBody(xml, nextValue + '<value>'.length)
    out.push(value)
    i = next
  }
  const arrayCloseFinal = xml.indexOf('</array>', i)
  return { value: out, next: arrayCloseFinal + '</array>'.length }
}

function parseStructBody(
  xml: string,
  start: number,
): { value: { [k: string]: XmlRpcValue }; next: number } {
  const out: { [k: string]: XmlRpcValue } = {}
  let i = start
  while (true) {
    const memberOpen = xml.indexOf('<member>', i)
    const structClose = xml.indexOf('</struct>', i)
    if (memberOpen < 0 || memberOpen > structClose) break
    // <member><name>FOO</name><value>...</value></member>
    const nameOpen = xml.indexOf('<name>', memberOpen)
    const nameClose = xml.indexOf('</name>', nameOpen)
    if (nameOpen < 0 || nameClose < 0) break
    const key = decodeEntities(xml.slice(nameOpen + '<name>'.length, nameClose))
    const valueOpen = xml.indexOf('<value>', nameClose)
    if (valueOpen < 0 || valueOpen > structClose) break
    const { value, next } = parseValueBody(xml, valueOpen + '<value>'.length)
    out[key] = value
    // Skip past the member close.
    const memberClose = xml.indexOf('</member>', next)
    i = memberClose < 0 ? next : memberClose + '</member>'.length
  }
  const structCloseFinal = xml.indexOf('</struct>', i)
  return { value: out, next: structCloseFinal + '</struct>'.length }
}

function coerceScalar(kind: string, raw: string): XmlRpcValue {
  switch (kind) {
    case 'string':
    case 'base64':
    case 'dateTime.iso8601':
      return raw
    case 'i4':
    case 'int':
    case 'i8': {
      const n = parseInt(raw.trim(), 10)
      return Number.isFinite(n) ? n : 0
    }
    case 'double': {
      const n = parseFloat(raw.trim())
      return Number.isFinite(n) ? n : 0
    }
    case 'boolean':
      return raw.trim() === '1'
    default:
      return raw
  }
}

function emptyValueForType(kind: string): XmlRpcValue {
  switch (kind) {
    case 'string':
    case 'base64':
    case 'dateTime.iso8601':
      return ''
    case 'i4':
    case 'int':
    case 'i8':
    case 'double':
      return 0
    case 'boolean':
      return false
    case 'array':
      return []
    case 'struct':
      return {}
    case 'nil':
      return null
    default:
      return null
  }
}

// ============================================================================
// Process info normalisation. The raw struct is `Record<string, unknown>`
// because XML-RPC has no schema; we coerce each field to its expected shape
// with safe defaults so downstream consumers can pattern-match without
// optional-chaining at every access.
// ============================================================================

function normaliseProcessInfo(raw: XmlRpcValue): SupervisordProcessInfo {
  const obj = (raw && typeof raw === 'object' && !Array.isArray(raw) ? raw : {}) as Record<
    string,
    XmlRpcValue
  >
  return {
    name: stringField(obj, 'name'),
    group: stringField(obj, 'group'),
    state: numberField(obj, 'state'),
    statename: stringField(obj, 'statename'),
    start: numberField(obj, 'start'),
    now: numberField(obj, 'now'),
    stop: numberField(obj, 'stop'),
    exitstatus: numberField(obj, 'exitstatus'),
    spawnerr: stringField(obj, 'spawnerr'),
    pid: numberField(obj, 'pid'),
    stdout_logfile: stringField(obj, 'stdout_logfile'),
    stderr_logfile: stringField(obj, 'stderr_logfile'),
    description: stringField(obj, 'description'),
  }
}

function stringField(o: Record<string, XmlRpcValue>, key: string): string {
  const v = o[key]
  if (typeof v === 'string') return v
  if (v === null || v === undefined) return ''
  return String(v)
}

function numberField(o: Record<string, XmlRpcValue>, key: string): number {
  const v = o[key]
  if (typeof v === 'number') return v
  if (typeof v === 'string') {
    const n = parseFloat(v)
    return Number.isFinite(n) ? n : 0
  }
  return 0
}

import { invalidEnvKeyMessage, isValidEnvKey } from './envVarKey'

export interface ParsedEnvEntry {
  key: string
  value: string
}

export interface SkippedDotEnvLine {
  line: number
  reason: string
  raw: string
}

export interface ParseDotEnvResult {
  entries: ParsedEnvEntry[]
  skipped: SkippedDotEnvLine[]
}

function stripInlineComment(value: string): string {
  for (let i = 0; i < value.length; i++) {
    if (value[i] !== '#') continue
    if (i === 0) return ''
    if (value[i - 1] === ' ' || value[i - 1] === '\t') {
      return value.slice(0, i).trimEnd()
    }
  }
  return value
}

function unquoteValue(raw: string): string | null {
  const trimmed = raw.trim()
  if (trimmed.length === 0) return ''

  const first = trimmed[0]
  if (first !== '"' && first !== "'") {
    return stripInlineComment(trimmed)
  }
  if (trimmed.length < 2 || trimmed[trimmed.length - 1] !== first) {
    return null
  }

  const inner = trimmed.slice(1, -1)
  if (first === "'") return inner

  let out = ''
  for (let i = 0; i < inner.length; i++) {
    const ch = inner[i]
    if (ch !== '\\') {
      out += ch
      continue
    }
    const next = inner[i + 1]
    if (next === undefined) return null
    switch (next) {
      case 'n':
        out += '\n'
        break
      case 'r':
        out += '\r'
        break
      case 't':
        out += '\t'
        break
      case '\\':
        out += '\\'
        break
      case '"':
        out += '"'
        break
      default:
        out += next
        break
    }
    i++
  }
  return out
}

/**
 * Parse dotenv-style text into key/value pairs. Comments and blank lines are
 * skipped; duplicate keys keep the last occurrence.
 */
export function parseDotEnv(text: string): ParseDotEnvResult {
  const skipped: SkippedDotEnvLine[] = []
  const byKey = new Map<string, ParsedEnvEntry>()

  const lines = text.replace(/\r\n/g, '\n').replace(/\r/g, '\n').split('\n')

  for (let index = 0; index < lines.length; index++) {
    const lineNumber = index + 1
    const raw = lines[index]
    const trimmed = raw.trim()

    if (trimmed.length === 0 || trimmed.startsWith('#')) continue

    let body = trimmed
    if (body.startsWith('export ')) {
      body = body.slice('export '.length).trimStart()
    }

    const eq = body.indexOf('=')
    if (eq <= 0) {
      skipped.push({ line: lineNumber, reason: 'Expected KEY=VALUE', raw })
      continue
    }

    const key = body.slice(0, eq).trim()
    const valueRaw = body.slice(eq + 1)

    if (!isValidEnvKey(key)) {
      skipped.push({ line: lineNumber, reason: invalidEnvKeyMessage(key), raw })
      continue
    }

    const value = unquoteValue(valueRaw)
    if (value === null) {
      skipped.push({ line: lineNumber, reason: 'Unclosed quote in value', raw })
      continue
    }

    if (value.length === 0) {
      skipped.push({ line: lineNumber, reason: 'Empty value skipped', raw })
      continue
    }

    if (value.includes('\n')) {
      skipped.push({ line: lineNumber, reason: 'Multiline values are not supported', raw })
      continue
    }

    byKey.set(key, { key, value })
  }

  return {
    entries: [...byKey.values()],
    skipped,
  }
}

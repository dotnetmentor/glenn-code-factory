import { describe, expect, it } from 'vitest'
import { parseDotEnv } from './parseDotEnv'

describe('parseDotEnv', () => {
  it('parses typical .env lines and skips comments', () => {
    const result = parseDotEnv(`
# comment
Jwt__Key=secret-key-minimum-32-characters!!
SystemSettings__EncryptionKey=abc123

ASPNETCORE_ENVIRONMENT=Development
`)
    expect(result.skipped).toEqual([])
    expect(result.entries).toEqual([
      { key: 'Jwt__Key', value: 'secret-key-minimum-32-characters!!' },
      { key: 'SystemSettings__EncryptionKey', value: 'abc123' },
      { key: 'ASPNETCORE_ENVIRONMENT', value: 'Development' },
    ])
  })

  it('supports export prefix, quotes, and inline comments', () => {
    const result = parseDotEnv(`
export DATABASE_URL="Host=localhost;Port=5432"
FOO=bar # trailing comment
`)
    expect(result.entries).toEqual([
      { key: 'DATABASE_URL', value: 'Host=localhost;Port=5432' },
      { key: 'FOO', value: 'bar' },
    ])
  })

  it('keeps the last duplicate key', () => {
    const result = parseDotEnv(`
A=one
A=two
`)
    expect(result.entries).toEqual([{ key: 'A', value: 'two' }])
  })

  it('skips empty values and invalid lines', () => {
    const result = parseDotEnv(`
OpenRouter__ApiKey=
not-a-line
1BAD=ok
`)
    expect(result.entries).toEqual([])
    expect(result.skipped.map((s) => s.reason)).toEqual([
      'Empty value skipped',
      'Expected KEY=VALUE',
      '"1BAD" is not valid. Start with a letter, then use letters, digits, or underscores only.',
    ])
  })
})

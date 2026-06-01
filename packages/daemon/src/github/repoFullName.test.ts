import { describe, expect, it } from 'vitest'

import { parseRepoFullName } from './repoFullName.js'

describe('parseRepoFullName', () => {
  it('happy path: strips .git suffix', () => {
    expect(parseRepoFullName('https://github.com/glenn/proj.git')).toBe('glenn/proj')
  })

  it('happy path: accepts URL without .git', () => {
    expect(parseRepoFullName('https://github.com/glenn/proj')).toBe('glenn/proj')
  })

  it('preserves owner and repo casing verbatim', () => {
    expect(parseRepoFullName('https://github.com/LovAble/Proj-Name.git')).toBe('LovAble/Proj-Name')
  })

  it('rejects scp-style git@github.com URLs', () => {
    expect(() => parseRepoFullName('git@github.com:glenn/proj.git')).toThrow(
      /SSH-style URLs are no longer supported/,
    )
  })

  it('rejects ssh:// scheme', () => {
    expect(() => parseRepoFullName('ssh://git@github.com/glenn/proj.git')).toThrow(
      /SSH-style URLs are no longer supported/,
    )
  })

  it('rejects http:// (must be https)', () => {
    expect(() => parseRepoFullName('http://github.com/glenn/proj.git')).toThrow(
      /expected https:\/\/github\.com/,
    )
  })

  it('rejects non-github hosts', () => {
    expect(() => parseRepoFullName('https://gitlab.com/glenn/proj.git')).toThrow(
      /expected https:\/\/github\.com/,
    )
  })

  it('rejects deep paths (org/repo/subpath)', () => {
    expect(() => parseRepoFullName('https://github.com/glenn/proj/tree/main')).toThrow(
      /exactly owner\/repo path segments/,
    )
  })

  it('rejects single-segment URLs', () => {
    expect(() => parseRepoFullName('https://github.com/glenn')).toThrow(
      /exactly owner\/repo path segments/,
    )
  })

  it('rejects empty owner or repo', () => {
    expect(() => parseRepoFullName('https://github.com//proj')).toThrow(
      /owner or repo segment is empty/,
    )
  })

  it('rejects empty string', () => {
    expect(() => parseRepoFullName('')).toThrow(/non-empty string/)
  })
})

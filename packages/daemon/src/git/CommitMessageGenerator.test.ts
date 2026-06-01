import { describe, expect, it } from 'vitest'

import { buildCommitMessage } from './CommitMessageGenerator.js'

describe('buildCommitMessage', () => {
  it('prefixes a short prompt with chore(turn): unchanged', () => {
    expect(buildCommitMessage({ userPrompt: 'add login form' })).toBe(
      'chore(turn): add login form',
    )
  })

  it('returns the fallback subject for an empty prompt', () => {
    expect(buildCommitMessage({ userPrompt: '' })).toBe('chore(turn): no description')
  })

  it('returns the fallback subject for an undefined prompt', () => {
    expect(buildCommitMessage({})).toBe('chore(turn): no description')
  })

  it('returns the fallback subject for whitespace-only prompts', () => {
    expect(buildCommitMessage({ userPrompt: '   \n\t  ' })).toBe(
      'chore(turn): no description',
    )
  })

  it('truncates a long prompt to 60 chars with an ellipsis', () => {
    const long = 'a'.repeat(120)
    const out = buildCommitMessage({ userPrompt: long })

    // Subject (the part after the prefix) must be ≤ 60 chars and end in `...`.
    const subject = out.slice('chore(turn): '.length)
    expect(subject.length).toBe(60)
    expect(subject.endsWith('...')).toBe(true)
    // 57 'a's + '...'
    expect(subject).toBe('a'.repeat(57) + '...')
  })

  it('collapses internal newlines and tabs to a single space', () => {
    const multiline = 'add login\nfix\tbug'
    expect(buildCommitMessage({ userPrompt: multiline })).toBe(
      'chore(turn): add login fix bug',
    )
  })

  it("always starts with the chore(turn): prefix", () => {
    for (const p of ['x', 'y'.repeat(200), 'multi\nline\nprompt']) {
      expect(buildCommitMessage({ userPrompt: p })).toMatch(/^chore\(turn\): /)
    }
  })
})

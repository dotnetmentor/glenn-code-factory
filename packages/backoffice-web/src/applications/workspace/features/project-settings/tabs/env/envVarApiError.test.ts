import { describe, expect, it } from 'vitest'
import { humaniseEnvVarApiError } from './envVarApiError'

describe('humaniseEnvVarApiError', () => {
  it('maps key_already_exists to a friendly message', () => {
    const err = { response: { data: { error: 'key_already_exists' } } }
    expect(humaniseEnvVarApiError(err, 'Jwt__Key')).toContain('Jwt__Key')
    expect(humaniseEnvVarApiError(err, 'Jwt__Key')).toContain('already exists')
    expect(humaniseEnvVarApiError(err, 'Jwt__Key')).not.toContain('key_already_exists')
  })

  it('maps invalid_key_format to a friendly message with the key', () => {
    const err = { response: { data: { error: 'invalid_key_format' } } }
    expect(humaniseEnvVarApiError(err, '1BAD')).toBe(
      '"1BAD" is not valid. Start with a letter, then use letters, digits, or underscores only.',
    )
  })
})

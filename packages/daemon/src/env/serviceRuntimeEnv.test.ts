import { describe, expect, it } from 'vitest'

import type { ServiceSpec } from '../runtime/SupervisordController.js'
import {
  allRequiredEnvPresent,
  mergeServiceRuntimeEnv,
  missingRequiredEnvKeys,
  readRequiredEnv,
} from './serviceRuntimeEnv.js'

describe('serviceRuntimeEnv', () => {
  const baseSpec: ServiceSpec = {
    name: 'dotnet-api',
    command: 'dotnet run',
    env: { ASPNETCORE_ENVIRONMENT: 'Development' },
  }

  it('readRequiredEnv returns declared keys from wire shape', () => {
    const spec = {
      ...baseSpec,
      requiredEnv: [{ key: 'Jwt__Key', secret: true }],
    }
    expect(readRequiredEnv(spec)).toEqual([{ key: 'Jwt__Key', secret: true }])
  })

  it('mergeServiceRuntimeEnv injects satisfied required keys into spec.env', () => {
    const spec = {
      ...baseSpec,
      requiredEnv: [{ key: 'Jwt__Key', secret: true }],
    }
    const env = new Map([['Jwt__Key', 'super-secret-key-at-least-32-chars-long']])
    const merged = mergeServiceRuntimeEnv(spec, env)
    expect(merged.env).toEqual({
      ASPNETCORE_ENVIRONMENT: 'Development',
      Jwt__Key: 'super-secret-key-at-least-32-chars-long',
    })
  })

  it('missingRequiredEnvKeys treats empty string as missing', () => {
    const spec = { requiredEnv: [{ key: 'Jwt__Key' }] }
    expect(missingRequiredEnvKeys(spec, new Map([['Jwt__Key', '']]))).toEqual([
      'Jwt__Key',
    ])
    expect(allRequiredEnvPresent(spec, new Map([['Jwt__Key', 'x'.repeat(32)]]))).toBe(
      true,
    )
  })

  it('required:false vars are suggested only — not blocking', () => {
    const spec = {
      requiredEnv: [
        { key: 'Jwt__Key', required: true },
        { key: 'OpenRouter__ApiKey', required: false },
      ],
    }
    const env = new Map([['Jwt__Key', 'x'.repeat(32)]])
    expect(missingRequiredEnvKeys(spec, env)).toEqual([])
    expect(allRequiredEnvPresent(spec, env)).toBe(true)
    const merged = mergeServiceRuntimeEnv(spec, env)
    expect(merged.env?.Jwt__Key).toBe('x'.repeat(32))
    expect(merged.env?.OpenRouter__ApiKey).toBeUndefined()
  })
})

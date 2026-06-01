import { describe, expect, it } from 'vitest'

import { MIN_PROTOCOL_VERSIONS, requireProtocol } from './versionRequirements.js'

describe('requireProtocol', () => {
  it('passes for methods with no entry in the table', () => {
    const check = requireProtocol('SomeUnregisteredMethod', '0.1.0')
    expect(check).toEqual({ ok: true })
  })

  it('passes when daemon version equals the minimum', () => {
    const required = MIN_PROTOCOL_VERSIONS['ApplyRuntimeSpecDelta']
    expect(required).toBe('0.4.0')
    const check = requireProtocol('ApplyRuntimeSpecDelta', '0.4.0')
    expect(check).toEqual({ ok: true })
  })

  it('passes when daemon version exceeds the minimum', () => {
    const check = requireProtocol('ApplyRuntimeSpecDelta', '1.2.3')
    expect(check).toEqual({ ok: true })
  })

  it('fails when daemon version is below the minimum', () => {
    const check = requireProtocol('ApplyRuntimeSpecDelta', '0.3.99')
    expect(check).toEqual({ ok: false, required: '0.4.0', current: '0.3.99' })
  })

  it('coerces a pre-release version string sensibly', () => {
    // semver.coerce('0.4.0-dev') → '0.4.0'
    const check = requireProtocol('ApplyRuntimeSpecDelta', '0.4.0-dev')
    expect(check).toEqual({ ok: true })
  })

  it("coerces a non-semver version like 'dev' to 0.0.0 and fails closed", () => {
    const check = requireProtocol('ApplyRuntimeSpecDelta', 'dev')
    expect(check).toEqual({ ok: false, required: '0.4.0', current: '0.0.0' })
  })

  it('coerces partial versions (1.2 → 1.2.0)', () => {
    // 1.2.0 is well above 0.4.0
    const check = requireProtocol('ApplyRuntimeSpecDelta', '1.2')
    expect(check).toEqual({ ok: true })
  })
})

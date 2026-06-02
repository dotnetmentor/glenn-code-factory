import { describe, expect, it, vi } from 'vitest'
import pino from 'pino'

import { BootstrapState } from '../bootstrap/BootstrapState.js'
import type { BootstrapPayloadV2 } from '../generated/signalr/Source.Features.RuntimeBootstrap.Contracts.js'
import { reapplyServicesForEnvKeys } from './reapplyServicesForEnvKeys.js'

function makePayload(): BootstrapPayloadV2 {
  return {
    version: 'v2',
    runtimeSpec: {
      version: 2,
      services: [
        {
          name: 'dotnet-api',
          command: 'dotnet run',
          requiredEnv: [{ key: 'Jwt__Key', secret: true }],
        } as BootstrapPayloadV2['runtimeSpec']['services'][number],
      ],
    },
    envVars: [],
    hooks: null,
    mcps: [],
    repo: null,
  }
}

describe('reapplyServicesForEnvKeys', () => {
  it('registers service with merged env when required key is set', async () => {
    const addService = vi.fn(async () => {})
    const executor = { run: vi.fn(async () => ({ exitCode: 0, stdout: '', stderr: '' })) }
    const state = new BootstrapState()
    state.setPayload(makePayload())
    const env = new Map([['Jwt__Key', 'secret-key-minimum-32-characters!!']])

    await reapplyServicesForEnvKeys(['Jwt__Key'], {
      bootstrapState: state,
      envVarManager: { current: () => env },
      supervisord: { addService },
      executor,
      logger: pino({ level: 'silent' }),
    })

    expect(addService).toHaveBeenCalledTimes(1)
    expect(addService.mock.calls[0]?.[0]?.env?.Jwt__Key).toBe(
      'secret-key-minimum-32-characters!!',
    )
    expect(executor.run).toHaveBeenCalledWith(
      'supervisorctl',
      ['restart', 'dotnet-api'],
      { allowNonZero: true },
    )
  })

  it('no-ops when required key is still missing', async () => {
    const addService = vi.fn(async () => {})
    const state = new BootstrapState()
    state.setPayload(makePayload())

    await reapplyServicesForEnvKeys(['Jwt__Key'], {
      bootstrapState: state,
      envVarManager: { current: () => new Map() },
      supervisord: { addService },
      executor: { run: vi.fn() },
      logger: pino({ level: 'silent' }),
    })

    expect(addService).not.toHaveBeenCalled()
  })
})

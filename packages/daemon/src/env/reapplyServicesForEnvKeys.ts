import type { Logger } from 'pino'

import type { BootstrapState } from '../bootstrap/BootstrapState.js'
import type { IExecutor } from '../runtime/IExecutor.js'
import type { ServiceSpec, SupervisordController } from '../runtime/SupervisordController.js'
import {
  allRequiredEnvPresent,
  mergeServiceRuntimeEnv,
  readRequiredEnv,
} from './serviceRuntimeEnv.js'

export interface ReapplyServicesForEnvKeysDeps {
  bootstrapState: BootstrapState
  envVarManager: { current(): ReadonlyMap<string, string> }
  supervisord: Pick<SupervisordController, 'addService'>
  executor: IExecutor
  logger: Logger
}

/**
 * After an env-var delta lands, re-render supervisord conf for every service
 * whose `requiredEnv` includes one of the changed keys AND whose required
 * vars are now fully satisfied. Registers the service when it was skipped at
 * bootstrap (env missing) or updates conf when values changed.
 */
export async function reapplyServicesForEnvKeys(
  changedKeys: readonly string[],
  deps: ReapplyServicesForEnvKeysDeps,
): Promise<void> {
  if (changedKeys.length === 0 || !deps.bootstrapState.hasPayload()) {
    return
  }

  const changed = new Set(changedKeys)
  const runtimeEnv = deps.envVarManager.current()
  const services = deps.bootstrapState.payload.runtimeSpec.services ?? []

  for (const raw of services) {
    const spec = raw as ServiceSpec
    const required = readRequiredEnv(spec)
    if (required.length === 0) continue
    if (!required.some((r) => changed.has(r.key))) continue
    if (!allRequiredEnvPresent(spec, runtimeEnv)) continue

    const merged = mergeServiceRuntimeEnv(spec, runtimeEnv, required)
    await deps.supervisord.addService(merged)

    try {
      await deps.executor.run('supervisorctl', ['restart', spec.name], {
        allowNonZero: true,
      })
    } catch (err) {
      deps.logger.debug(
        { err, service: spec.name },
        'reapplyServicesForEnvKeys: supervisorctl restart returned non-zero (may be first registration)',
      )
    }

    deps.logger.info(
      { service: spec.name, keys: required.map((r) => r.key) },
      'reapplied supervisord conf after required env var update',
    )
  }
}

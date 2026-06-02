type ApiErrorBody = { error?: string }

export function readEnvVarApiErrorCode(error: unknown): string | null {
  const maybe = error as { response?: { data?: ApiErrorBody } } | undefined
  return maybe?.response?.data?.error ?? null
}

import { invalidEnvKeyMessage } from './envVarKey'

export function humaniseEnvVarApiError(error: unknown, key?: string): string {
  const code = readEnvVarApiErrorCode(error)
  const named = key ? `"${key}"` : 'This variable'

  switch (code) {
    case 'key_already_exists':
      return `${named} already exists on this branch. Edit it from the list below, or paste a .env file to update existing keys.`
    case 'invalid_key_format':
      return invalidEnvKeyMessage(key ?? '')
    case 'invalid_plaintext':
      return 'Values cannot contain line breaks.'
    case 'not_found':
      return `${named} was not found on this branch. Refresh the page and try again.`
    default:
      return "Couldn't save the variable. Please try again."
  }
}

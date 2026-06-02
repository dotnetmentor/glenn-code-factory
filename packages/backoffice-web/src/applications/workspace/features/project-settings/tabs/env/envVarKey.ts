/** Matches backend SecretValidation — letters, digits, underscores; .NET-style keys OK. */
export const ENV_KEY_PATTERN = /^[A-Za-z][A-Za-z0-9_]*$/

export const ENV_KEY_MAX_LENGTH = 200

export function isValidEnvKey(key: string): boolean {
  return (
    key.length > 0 &&
    key.length <= ENV_KEY_MAX_LENGTH &&
    ENV_KEY_PATTERN.test(key)
  )
}

export function invalidEnvKeyMessage(key: string): string {
  const trimmed = key.trim()
  const named = trimmed.length > 0 ? `"${trimmed}"` : 'That key name'
  return `${named} is not valid. Start with a letter, then use letters, digits, or underscores only.`
}

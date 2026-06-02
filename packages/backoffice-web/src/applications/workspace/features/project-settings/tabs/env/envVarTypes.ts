export type EnvVarScope = 'project' | 'branch'

/** Unified row shape for project defaults and branch overrides in the drawer. */
export interface EnvVarListItem {
  key: string
  isSecret: boolean
  version: number
  updatedAt: string
  scope: EnvVarScope
  /** Plaintext for non-secret rows only. */
  value?: string | null
}

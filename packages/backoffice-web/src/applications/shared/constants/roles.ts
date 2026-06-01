export const ApplicationRoles = {
  SuperAdmin: 'SuperAdmin',
  TenantAdmin: 'TenantAdmin',
  WorkspaceUser: 'WorkspaceUser',
  Example: 'Example'
} as const

export type ApplicationRole = typeof ApplicationRoles[keyof typeof ApplicationRoles]

export const AVAILABLE_ROLES: ApplicationRole[] = Object.values(ApplicationRoles)

interface RoleConfigItem {
  color: string
  backgroundColor: string
  label: string
  priority: number
}

const RoleConfig: Record<ApplicationRole, RoleConfigItem> = {
  [ApplicationRoles.SuperAdmin]: {
    color: '#d32f2f',
    backgroundColor: '#d32f2f',
    label: 'Admin',
    priority: 3
  },
  [ApplicationRoles.TenantAdmin]: {
    color: '#7b1fa2',
    backgroundColor: '#7b1fa2',
    label: 'Tenant Admin',
    priority: 2
  },
  [ApplicationRoles.WorkspaceUser]: {
    color: '#00897b',
    backgroundColor: '#00897b',
    label: 'Workspace User',
    priority: 1
  },
  [ApplicationRoles.Example]: {
    color: '#1976d2',
    backgroundColor: '#1976d2',
    label: 'Example User',
    priority: 0
  }
}

const defaultRoleConfig: RoleConfigItem = {
  color: '#757575',
  backgroundColor: '#757575',
  label: 'Unknown',
  priority: 0
}

function isApplicationRole(role: string): role is ApplicationRole {
  return Object.values(ApplicationRoles).includes(role as ApplicationRole)
}

export function getRoleConfig(role: string): RoleConfigItem {
  if (isApplicationRole(role)) {
    return RoleConfig[role]
  }
  return { ...defaultRoleConfig, label: role }
}

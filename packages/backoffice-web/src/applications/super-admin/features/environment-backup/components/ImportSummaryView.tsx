import { Alert, AlertTitle, Box, Stack, Typography } from '@mui/material'
import type { EnvironmentImportSummary } from '../../../../../api/queries-commands'

type Props = {
  summary: EnvironmentImportSummary
}

const ROWS: { key: keyof EnvironmentImportSummary; label: string }[] = [
  { key: 'users', label: 'Users' },
  { key: 'systemSettings', label: 'System settings' },
  { key: 'workspaces', label: 'Workspaces' },
  { key: 'workspaceMemberships', label: 'Workspace memberships' },
  { key: 'workspaceInvites', label: 'Workspace invites' },
  { key: 'workspaceSpecs', label: 'Workspace specs' },
  { key: 'githubInstallations', label: 'GitHub installations' },
  { key: 'projects', label: 'Projects' },
  { key: 'projectBranches', label: 'Project branches' },
  { key: 'projectSecrets', label: 'Project secrets' },
  { key: 'projectAgentPermissions', label: 'Project agent permissions' },
  { key: 'specifications', label: 'Specifications' },
  { key: 'kanbanCards', label: 'Kanban cards' },
  { key: 'kanbanSubtasks', label: 'Kanban subtasks' },
]

export function ImportSummaryView({ summary }: Props) {
  return (
    <Stack spacing={2}>
      <Alert severity="success">
        <AlertTitle>Import complete</AlertTitle>
        Restored from backup version <strong>{summary.version}</strong>.
      </Alert>

      {summary.usersWithoutPasswordHash > 0 && (
        <Alert severity="warning">
          <AlertTitle>
            {summary.usersWithoutPasswordHash} user
            {summary.usersWithoutPasswordHash === 1 ? '' : 's'} restored without a
            password hash
          </AlertTitle>
          These accounts exist but cannot sign in with a password. They will need to
          reset their password (or sign in via their identity provider) before use.
        </Alert>
      )}

      <Box
        sx={{
          display: 'grid',
          gridTemplateColumns: { xs: '1fr 1fr', sm: 'repeat(3, 1fr)' },
          gap: 1.5,
        }}
      >
        {ROWS.map((row) => (
          <Box
            key={row.key}
            sx={{
              border: 1,
              borderColor: 'divider',
              borderRadius: 1,
              px: 2,
              py: 1.5,
            }}
          >
            <Typography variant="h5" component="div">
              {summary[row.key]}
            </Typography>
            <Typography variant="caption" color="text.secondary">
              {row.label}
            </Typography>
          </Box>
        ))}
      </Box>
    </Stack>
  )
}

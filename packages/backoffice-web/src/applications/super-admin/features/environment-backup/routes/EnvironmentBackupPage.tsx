import { Box, Stack, Typography } from '@mui/material'
import { useEnvironmentBackup } from '../hooks/useEnvironmentBackup'
import { ExportPanel } from '../components/ExportPanel'
import { ImportPanel } from '../components/ImportPanel'

export function EnvironmentBackupPage() {
  const backup = useEnvironmentBackup()

  return (
    <Stack spacing={4}>
      <Box>
        <Typography variant="h4" component="h1" sx={{ mb: 1 }}>
          Environment Backup
        </Typography>
        <Typography variant="body1" color="text.secondary">
          Export the whole environment as a single JSON blob for disaster recovery or
          cloning, and import a blob to reconstruct an environment in one action. Super
          admins only.
        </Typography>
      </Box>

      <ExportPanel backup={backup} />
      <ImportPanel backup={backup} />
    </Stack>
  )
}

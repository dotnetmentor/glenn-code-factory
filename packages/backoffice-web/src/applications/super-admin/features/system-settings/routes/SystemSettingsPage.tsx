import {
  Alert,
  Box,
  CircularProgress,
  Stack,
  Typography,
} from '@mui/material'
import {
  useGetApiSystemSettings,
  useGetApiSystemSettingsCategories,
} from '../../../../../api/queries-commands'
import { SystemSettingsTabs } from '../components/SystemSettingsTabs'

export function SystemSettingsPage() {
  const categoriesQuery = useGetApiSystemSettingsCategories()
  const settingsQuery = useGetApiSystemSettings()

  const isLoading = categoriesQuery.isLoading || settingsQuery.isLoading
  const error = categoriesQuery.error ?? settingsQuery.error
  const categories = categoriesQuery.data ?? []
  const settings = settingsQuery.data ?? []

  return (
    <Stack spacing={4}>
          <Box>
            <Typography variant="h4" component="h1" sx={{ mb: 1 }}>
              System Settings
            </Typography>
            <Typography variant="body1" color="text.secondary">
              Configure system-level integrations. Only super admins can see this page.
            </Typography>
          </Box>

          {isLoading && (
            <Box sx={{ display: 'flex', justifyContent: 'center', py: 6 }}>
              <CircularProgress />
            </Box>
          )}

          {!isLoading && error && (
            <Alert severity="error">
              Failed to load system settings:{' '}
              {error instanceof Error ? error.message : 'Unknown error'}
            </Alert>
          )}

          {!isLoading && !error && (
            <SystemSettingsTabs categories={categories} settings={settings} />
          )}
        </Stack>
  )
}

import { useMemo } from 'react'
import { useLocation, useNavigate, useParams } from 'react-router-dom'
import { Box, Tab, Tabs } from '@mui/material'
import { EnvironmentVariablesPage } from './EnvironmentVariablesPage'
import { CredentialsPage } from './CredentialsPage'

type SettingsTab = 'environment' | 'credentials'

/**
 * Minimal shell for project-level settings. Hosts the Environment Variables
 * and Credentials (BYOK) tabs. Future settings tabs (e.g. Integrations,
 * Members, etc.) can be added here without re-routing.
 */
export function ProjectSettingsPage() {
  const { projectId } = useParams<{ projectId: string }>()
  const location = useLocation()
  const navigate = useNavigate()

  const currentTab: SettingsTab = useMemo(() => {
    if (location.pathname.endsWith('/settings/credentials')) return 'credentials'
    return 'environment'
  }, [location.pathname])

  const handleTabChange = (_e: React.SyntheticEvent, value: SettingsTab) => {
    if (value === 'environment') {
      navigate(`/super-admin/projects/${projectId}/settings/environment`)
    } else if (value === 'credentials') {
      navigate(`/super-admin/projects/${projectId}/settings/credentials`)
    }
  }

  return (
    <Box>
      <Box sx={{ borderBottom: 1, borderColor: 'divider', mb: 3 }}>
        <Tabs value={currentTab} onChange={handleTabChange}>
          <Tab label="Environment" value="environment" />
          <Tab label="Credentials" value="credentials" />
        </Tabs>
      </Box>
      {currentTab === 'environment' && <EnvironmentVariablesPage />}
      {currentTab === 'credentials' && <CredentialsPage />}
    </Box>
  )
}

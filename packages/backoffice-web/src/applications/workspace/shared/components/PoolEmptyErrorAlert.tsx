import OpenInNewIcon from '@mui/icons-material/OpenInNew'
import { Alert, Box, Typography } from '@mui/material'
import { bodySx, workspaceAccent, workspaceFontFamily } from '../designTokens'
import { SUBDOMAINS_ADMIN_PATH } from '../poolEmptyError'

/**
 * Inline error when preview subdomains are exhausted — points Super Admins
 * at the subdomains pool UI.
 */
export function PoolEmptyErrorAlert() {
  return (
    <Alert severity="error" variant="quiet">
      <Typography sx={{ ...bodySx, mb: 1 }}>
        No preview subdomains are available, so this project cannot be created yet.
        A Super Admin needs to add subdomains to the pool.
      </Typography>
      <Box
        component="a"
        href={SUBDOMAINS_ADMIN_PATH}
        target="_blank"
        rel="noopener noreferrer"
        sx={{
          color: workspaceAccent.ink,
          fontFamily: workspaceFontFamily.sans,
          fontWeight: 500,
          fontSize: '0.875rem',
          textDecoration: 'none',
          display: 'inline-flex',
          alignItems: 'center',
          gap: 0.5,
          '&:hover': { textDecoration: 'underline' },
        }}
      >
        Open Subdomains admin
        <OpenInNewIcon sx={{ fontSize: 14 }} />
      </Box>
    </Alert>
  )
}

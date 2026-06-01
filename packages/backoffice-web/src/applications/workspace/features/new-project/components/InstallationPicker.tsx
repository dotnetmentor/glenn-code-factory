import {
  Avatar,
  Box,
  Chip,
  FormControl,
  InputLabel,
  ListItemAvatar,
  ListItemText,
  MenuItem,
  Select,
  Stack,
  Typography,
  type SelectChangeEvent,
} from '@mui/material'
import type { GithubInstallationListItem } from '../../../../../api/queries-commands'

interface InstallationPickerProps {
  installations: GithubInstallationListItem[]
  value: string | null
  onChange: (installationId: string) => void
}

/**
 * Dropdown that picks which `GithubInstallation` to browse repos under. The
 * caller is expected to skip rendering this entirely when there is exactly one
 * installation (auto-selection happens in the page).
 */
export function InstallationPicker({ installations, value, onChange }: InstallationPickerProps) {
  const handleChange = (event: SelectChangeEvent<string>) => {
    onChange(event.target.value)
  }

  return (
    <FormControl fullWidth size="small" sx={{ maxWidth: 480 }}>
      <InputLabel id="installation-picker-label">GitHub installation</InputLabel>
      <Select
        labelId="installation-picker-label"
        label="GitHub installation"
        value={value ?? ''}
        onChange={handleChange}
        renderValue={(selected) => {
          const found = installations.find((i) => i.id === selected)
          if (!found) return ''
          return (
            <Stack direction="row" spacing={1} alignItems="center">
              <Avatar
                src={found.accountAvatarUrl ?? undefined}
                alt={found.accountLogin}
                sx={{ width: 24, height: 24 }}
                variant="rounded"
              >
                {found.accountLogin.charAt(0).toUpperCase()}
              </Avatar>
              <Typography variant="body2">{found.accountLogin}</Typography>
            </Stack>
          )
        }}
      >
        {installations.map((installation) => (
          <MenuItem key={installation.id} value={installation.id}>
            <ListItemAvatar sx={{ minWidth: 40 }}>
              <Avatar
                src={installation.accountAvatarUrl ?? undefined}
                alt={installation.accountLogin}
                sx={{ width: 28, height: 28 }}
                variant="rounded"
              >
                {installation.accountLogin.charAt(0).toUpperCase()}
              </Avatar>
            </ListItemAvatar>
            <ListItemText
              primary={
                <Stack direction="row" spacing={1} alignItems="center">
                  <Typography variant="body2" fontWeight={500}>
                    {installation.accountLogin}
                  </Typography>
                  <Chip size="small" label={installation.accountType} variant="outlined" />
                  {installation.suspended && (
                    <Chip size="small" label="Suspended" color="warning" />
                  )}
                </Stack>
              }
              secondary={
                <Box component="span" sx={{ color: 'text.secondary', fontSize: '0.75rem' }}>
                  {installation.repoCount}{' '}
                  {installation.repoCount === 1 ? 'repository' : 'repositories'}
                </Box>
              }
            />
          </MenuItem>
        ))}
      </Select>
    </FormControl>
  )
}

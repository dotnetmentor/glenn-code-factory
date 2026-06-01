import { Box, Paper, Stack, Typography } from '@mui/material'
import InboxOutlinedIcon from '@mui/icons-material/InboxOutlined'

interface EmptyStateProps {
  title: string
  description?: string
}

/**
 * Empty-state panel rendered when {@code summary.count === 0} for the
 * selected window. Replaces the summary/chart/table block so operators don't
 * have to mentally distinguish "zero wakes" from "things took zero ms".
 */
export function EmptyState({ title, description }: EmptyStateProps) {
  return (
    <Paper variant="outlined" sx={{ p: 6 }}>
      <Stack alignItems="center" spacing={1.5}>
        <Box
          sx={{
            color: 'text.disabled',
            display: 'flex',
            alignItems: 'center',
            justifyContent: 'center',
          }}
        >
          <InboxOutlinedIcon sx={{ fontSize: 56 }} />
        </Box>
        <Typography variant="h6" color="text.secondary">
          {title}
        </Typography>
        {description && (
          <Typography
            variant="body2"
            color="text.secondary"
            sx={{ maxWidth: 480, textAlign: 'center' }}
          >
            {description}
          </Typography>
        )}
      </Stack>
    </Paper>
  )
}

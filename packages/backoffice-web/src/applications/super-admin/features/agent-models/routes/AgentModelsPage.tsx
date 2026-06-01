import {
  Alert,
  Box,
  CircularProgress,
  Stack,
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableRow,
  Typography,
} from '@mui/material'
import SmartToyIcon from '@mui/icons-material/SmartToy'
import { useGetApiCursorModelsActive } from '../../../../../api/queries-commands'

/**
 * Read-only catalog of active Cursor SDK models exposed to project owners.
 * Admin CRUD for the catalog is seeded via migrations — there is no write API yet.
 */
export function AgentModelsPage() {
  const listQuery = useGetApiCursorModelsActive({
    query: { staleTime: 30_000 },
  })
  const rows = listQuery.data ?? []

  return (
    <Stack spacing={3} sx={{ p: 3, maxWidth: 960 }}>
      <Stack direction="row" spacing={1.5} alignItems="center">
        <SmartToyIcon color="primary" />
        <Typography variant="h5" component="h1">
          Cursor models
        </Typography>
      </Stack>

      <Typography variant="body2" color="text.secondary">
        Active models available in project settings and the chat composer. The
        platform runs exclusively on the Cursor SDK local runtime.
      </Typography>

      {listQuery.isError && (
        <Alert severity="error">Could not load the Cursor model catalog.</Alert>
      )}

      {listQuery.isLoading ? (
        <Box sx={{ display: 'flex', justifyContent: 'center', py: 6 }}>
          <CircularProgress size={32} />
        </Box>
      ) : (
        <Table size="small">
          <TableHead>
            <TableRow>
              <TableCell>Display name</TableCell>
              <TableCell>Slug</TableCell>
              <TableCell>Sort</TableCell>
            </TableRow>
          </TableHead>
          <TableBody>
            {rows.map((row) => (
              <TableRow key={row.id}>
                <TableCell>{row.displayName}</TableCell>
                <TableCell>{row.slug}</TableCell>
                <TableCell>{row.sortOrder}</TableCell>
              </TableRow>
            ))}
            {rows.length === 0 && (
              <TableRow>
                <TableCell colSpan={3}>
                  <Typography variant="body2" color="text.secondary">
                    No active models in the catalog.
                  </Typography>
                </TableCell>
              </TableRow>
            )}
          </TableBody>
        </Table>
      )}
    </Stack>
  )
}

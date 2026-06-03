import { useState } from 'react'
import {
  Box,
  Chip,
  CircularProgress,
  Paper,
  Stack,
  Table,
  TableBody,
  TableCell,
  TableContainer,
  TableHead,
  TablePagination,
  TableRow,
  TextField,
  Typography,
} from '@mui/material'
import { useGetApiWaitlist } from '../../../../../api/queries-commands'
import { formatSwedishDateTime } from '../../../../shared/utils'

/**
 * Super-admin view of public waitlist signups, newest first. Read-only — the
 * signups are captured by the anonymous landing-page form.
 */
export function WaitlistPage() {
  const [page, setPage] = useState(0) // MUI is 0-based; API is 1-based.
  const [pageSize, setPageSize] = useState(50)
  const [search, setSearch] = useState('')

  const { data, isLoading } = useGetApiWaitlist({
    page: page + 1,
    pageSize,
    search: search.trim() || undefined,
  })

  const items = data?.items ?? []
  const total = data?.totalCount ?? 0

  return (
    <Box sx={{ px: 1, py: 2 }}>
      <Stack direction="row" alignItems="baseline" spacing={1.5} sx={{ mb: 0.5 }}>
        <Typography variant="h5" sx={{ fontWeight: 600, letterSpacing: '-0.01em' }}>
          Waitlist
        </Typography>
        <Typography variant="body2" sx={{ color: 'text.secondary' }}>
          {total} {total === 1 ? 'signup' : 'signups'}
        </Typography>
      </Stack>
      <Typography variant="body2" sx={{ color: 'text.secondary', mb: 2.5 }}>
        Interest captured from the public landing page.
      </Typography>

      <TextField
        placeholder="Search by email…"
        value={search}
        onChange={(e) => {
          setSearch(e.target.value)
          setPage(0)
        }}
        size="small"
        sx={{ mb: 2, width: { xs: '100%', sm: 320 } }}
      />

      <TableContainer component={Paper} variant="outlined" sx={{ borderRadius: 2 }}>
        <Table size="small">
          <TableHead>
            <TableRow>
              <TableCell sx={{ fontWeight: 600 }}>Email</TableCell>
              <TableCell sx={{ fontWeight: 600 }}>What they'd build</TableCell>
              <TableCell sx={{ fontWeight: 600 }}>Source</TableCell>
              <TableCell sx={{ fontWeight: 600 }} align="right">Signed up</TableCell>
            </TableRow>
          </TableHead>
          <TableBody>
            {isLoading ? (
              <TableRow>
                <TableCell colSpan={4} align="center" sx={{ py: 5 }}>
                  <CircularProgress size={20} />
                </TableCell>
              </TableRow>
            ) : items.length === 0 ? (
              <TableRow>
                <TableCell colSpan={4} align="center" sx={{ py: 5, color: 'text.secondary' }}>
                  No signups yet.
                </TableCell>
              </TableRow>
            ) : (
              items.map((item) => (
                <TableRow key={item.id} hover>
                  <TableCell sx={{ fontWeight: 500 }}>{item.email}</TableCell>
                  <TableCell sx={{ color: 'text.secondary', maxWidth: 360 }}>
                    {item.note || <span style={{ opacity: 0.5 }}>—</span>}
                  </TableCell>
                  <TableCell>
                    {item.source ? <Chip label={item.source} size="small" variant="outlined" /> : '—'}
                  </TableCell>
                  <TableCell align="right" sx={{ color: 'text.secondary', whiteSpace: 'nowrap' }}>
                    {formatSwedishDateTime(item.createdAt)}
                  </TableCell>
                </TableRow>
              ))
            )}
          </TableBody>
        </Table>
        <TablePagination
          component="div"
          count={total}
          page={page}
          onPageChange={(_, p) => setPage(p)}
          rowsPerPage={pageSize}
          onRowsPerPageChange={(e) => {
            setPageSize(parseInt(e.target.value, 10))
            setPage(0)
          }}
          rowsPerPageOptions={[25, 50, 100]}
        />
      </TableContainer>
    </Box>
  )
}

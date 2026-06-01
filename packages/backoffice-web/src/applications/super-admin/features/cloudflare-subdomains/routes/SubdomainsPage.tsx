import { useMemo, useState } from 'react'
import {
  Alert,
  Box,
  Button,
  CircularProgress,
  Stack,
  Table,
  TableBody,
  TableCell,
  TableContainer,
  TableHead,
  TableRow,
  Typography,
} from '@mui/material'
import AddIcon from '@mui/icons-material/Add'
import { useGetApiCloudflareSubdomains } from '../../../../../api/queries-commands'
import { SubdomainRow } from '../components/SubdomainRow'
import { BatchCreateDialog } from '../components/BatchCreateDialog'

/**
 * Super-admin subdomain pool page. Lists every Cloudflare preview subdomain
 * owned by the platform with its current status (Available / Assigned /
 * Releasing) and — when assigned — the owning project + branch.
 *
 * Pool refill is always manual: clicking "Batch create" provisions N new
 * tunnels via the Cloudflare API. Released subdomains are never returned to
 * the pool.
 */
export function SubdomainsPage() {
  const [batchOpen, setBatchOpen] = useState(false)

  const subdomainsQuery = useGetApiCloudflareSubdomains()

  const subdomains = useMemo(
    () => subdomainsQuery.data ?? [],
    [subdomainsQuery.data],
  )

  const counts = useMemo(() => {
    let available = 0
    let assigned = 0
    let releasing = 0
    for (const s of subdomains) {
      if (s.status === 'Available') available++
      else if (s.status === 'Assigned') assigned++
      else if (s.status === 'Releasing') releasing++
    }
    return { available, assigned, releasing }
  }, [subdomains])

  const isLoading = subdomainsQuery.isLoading
  const isError = subdomainsQuery.isError
  const hasSubdomains = subdomains.length > 0

  return (
    <>
      <Stack spacing={4}>
            <Box
              sx={{
                display: 'flex',
                justifyContent: 'space-between',
                alignItems: 'flex-start',
              }}
            >
              <Box>
                <Typography variant="h4" component="h1" sx={{ mb: 1 }}>
                  Subdomains
                </Typography>
                <Typography variant="body1" color="text.secondary">
                  {hasSubdomains
                    ? `${counts.available} available · ${counts.assigned} assigned${
                        counts.releasing > 0
                          ? ` · ${counts.releasing} releasing`
                          : ''
                      }`
                    : 'Preview tunnels minted in advance and assigned to branches as they are created.'}
                </Typography>
              </Box>
              <Button
                variant="contained"
                startIcon={<AddIcon />}
                onClick={() => setBatchOpen(true)}
                size="large"
                sx={{ flexShrink: 0 }}
              >
                Batch create
              </Button>
            </Box>

            {isError && (
              <Alert severity="error">Could not load subdomains.</Alert>
            )}

            {isLoading && !isError && (
              <Box sx={{ display: 'flex', justifyContent: 'center', py: 6 }}>
                <CircularProgress />
              </Box>
            )}

            {!isLoading && !isError && !hasSubdomains && (
              <Box sx={{ py: 6, textAlign: 'center' }}>
                <Typography variant="body1" color="text.secondary" sx={{ mb: 2 }}>
                  No subdomains in the pool yet. Click Batch create to provision
                  a batch of Cloudflare preview tunnels.
                </Typography>
                <Button
                  variant="outlined"
                  startIcon={<AddIcon />}
                  onClick={() => setBatchOpen(true)}
                >
                  Batch create
                </Button>
              </Box>
            )}

            {!isLoading && !isError && hasSubdomains && (
              <TableContainer>
                <Table size="small">
                  <TableHead>
                    <TableRow>
                      <TableCell>Hostname</TableCell>
                      <TableCell>Status</TableCell>
                      <TableCell>Assigned to</TableCell>
                    </TableRow>
                  </TableHead>
                  <TableBody>
                    {subdomains.map((subdomain) => (
                      <SubdomainRow
                        key={subdomain.id}
                        subdomain={subdomain}
                      />
                    ))}
                  </TableBody>
                </Table>
              </TableContainer>
            )}
          </Stack>

      <BatchCreateDialog
        open={batchOpen}
        onClose={() => setBatchOpen(false)}
      />
    </>
  )
}

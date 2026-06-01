import { useState } from 'react'
import {
  Alert,
  Box,
  Button,
  CircularProgress,
  Dialog,
  DialogActions,
  DialogContent,
  DialogTitle,
  Stack,
  TextField,
  Typography,
} from '@mui/material'
import { useQueryClient } from '@tanstack/react-query'
import {
  getGetApiCloudflareSubdomainsQueryKey,
  usePostApiCloudflareSubdomainsBatch,
  type ProblemDetails,
} from '../../../../../api/queries-commands'
import { useNotification } from '../../../../shared/contexts/NotificationContext'

const DEFAULT_COUNT = 5
const MIN_COUNT = 1
const MAX_COUNT = 50

function readErrorDetail(err: unknown): string | null {
  const maybe = err as { response?: { data?: ProblemDetails } } | undefined
  return maybe?.response?.data?.detail ?? maybe?.response?.data?.title ?? null
}

export interface BatchCreateDialogProps {
  open: boolean
  onClose: () => void
}

export function BatchCreateDialog({ open, onClose }: BatchCreateDialogProps) {
  const queryClient = useQueryClient()
  const { showSuccess, showError } = useNotification()
  const batchMutation = usePostApiCloudflareSubdomainsBatch()

  const [countText, setCountText] = useState<string>(String(DEFAULT_COUNT))

  const parsedCount = Number(countText)
  const isValidCount =
    Number.isInteger(parsedCount) &&
    parsedCount >= MIN_COUNT &&
    parsedCount <= MAX_COUNT

  const isRunning = batchMutation.isPending

  const handleClose = () => {
    if (isRunning) return
    setCountText(String(DEFAULT_COUNT))
    onClose()
  }

  const handleCreate = async () => {
    if (!isValidCount || isRunning) return
    try {
      const result = await batchMutation.mutateAsync({
        data: { count: parsedCount },
      })
      await queryClient.invalidateQueries({
        queryKey: getGetApiCloudflareSubdomainsQueryKey(),
      })
      const { successCount, failedCount } = result
      if (failedCount > 0) {
        showError(
          `${successCount} subdomain${successCount === 1 ? '' : 's'} created, ${failedCount} failed.`,
        )
      } else {
        showSuccess(
          `${successCount} subdomain${successCount === 1 ? '' : 's'} created.`,
        )
      }
      setCountText(String(DEFAULT_COUNT))
      onClose()
    } catch (err) {
      showError(readErrorDetail(err) ?? 'Could not batch-create subdomains.')
    }
  }

  return (
    <Dialog open={open} onClose={handleClose} maxWidth="xs" fullWidth>
      <DialogTitle>Batch create subdomains</DialogTitle>
      <DialogContent>
        <Stack spacing={2} sx={{ mt: 1 }}>
          <Typography variant="body2" color="text.secondary">
            Provisions Cloudflare Tunnels with random 8-character hostnames
            under your configured base domain. Each one is created via the
            Cloudflare API — this can take a few seconds.
          </Typography>
          <Box>
            <TextField
              id="batch-count"
              label={`How many? (${MIN_COUNT}\u2013${MAX_COUNT})`}
              type="number"
              size="small"
              fullWidth
              autoFocus
              value={countText}
              onChange={(e) => setCountText(e.target.value)}
              disabled={isRunning}
              inputProps={{
                'aria-label': 'Number of subdomains to create',
                min: MIN_COUNT,
                max: MAX_COUNT,
                step: 1,
              }}
            />
          </Box>
          {!isValidCount && countText.length > 0 && (
            <Alert severity="warning">
              Enter a whole number between {MIN_COUNT} and {MAX_COUNT}.
            </Alert>
          )}
          {isRunning && (
            <Stack direction="row" spacing={1.5} alignItems="center">
              <CircularProgress size={16} />
              <Typography variant="caption" color="text.secondary">
                Provisioning tunnels via Cloudflare…
              </Typography>
            </Stack>
          )}
        </Stack>
      </DialogContent>
      <DialogActions>
        <Button onClick={handleClose} disabled={isRunning}>
          Cancel
        </Button>
        <Button
          variant="contained"
          onClick={handleCreate}
          disabled={!isValidCount || isRunning}
        >
          {isRunning ? 'Creating…' : 'Create'}
        </Button>
      </DialogActions>
    </Dialog>
  )
}

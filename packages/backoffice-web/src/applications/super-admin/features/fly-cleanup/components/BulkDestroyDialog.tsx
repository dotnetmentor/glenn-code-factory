import { useEffect, useState } from 'react'
import {
  Alert,
  Box,
  Button,
  CircularProgress,
  Collapse,
  Dialog,
  DialogActions,
  DialogContent,
  DialogTitle,
  FormControlLabel,
  IconButton,
  Stack,
  Switch,
  TextField,
  Typography,
} from '@mui/material'
import ExpandLessIcon from '@mui/icons-material/ExpandLess'
import ExpandMoreIcon from '@mui/icons-material/ExpandMore'
import type { BulkDestroyResponse } from '@/api/queries-commands'

const CONFIRM_TOKEN = 'DELETE'

export interface BulkDestroyItem {
  id: string
  name: string
  isOrphan: boolean
  linkedProjectName?: string | null
  linkedBranchName?: string | null
}

export interface BulkDestroyDialogProps {
  open: boolean
  onClose: () => void
  /** "machines" | "volumes" — for the title + button labels. */
  resourceKind: 'machines' | 'volumes'
  items: BulkDestroyItem[]
  /** Whether to show the {@code force} toggle. Machines only. */
  showForce: boolean
  isSubmitting: boolean
  lastResult: BulkDestroyResponse | null
  /** Called with {@code force} (true | false). Volumes ignore force server-side. */
  onConfirm: (force: boolean) => void
}

/**
 * Confirmation modal for the bulk-destroy flow. Forces the operator to type
 * "DELETE" before the submit button enables — small friction speed-bump that
 * makes "oh shit I selected 80 things" much less likely. Shows the failure
 * breakdown inline after the request resolves so partial failures don't
 * disappear into a snackbar.
 */
export function BulkDestroyDialog({
  open,
  onClose,
  resourceKind,
  items,
  showForce,
  isSubmitting,
  lastResult,
  onConfirm,
}: BulkDestroyDialogProps) {
  const [typed, setTyped] = useState('')
  const [force, setForce] = useState(false)
  const [failureExpanded, setFailureExpanded] = useState(true)

  // Reset local state every time the dialog opens fresh.
  useEffect(() => {
    if (open) {
      setTyped('')
      setForce(false)
      setFailureExpanded(true)
    }
  }, [open])

  const confirmed = typed === CONFIRM_TOKEN
  const canSubmit = confirmed && items.length > 0 && !isSubmitting

  const linkedItems = items.filter((it) => !it.isOrphan)
  const orphanItems = items.filter((it) => it.isOrphan)

  const handleClose = () => {
    if (isSubmitting) return
    onClose()
  }

  return (
    <Dialog open={open} onClose={handleClose} maxWidth="sm" fullWidth>
      <DialogTitle>
        Delete {items.length} {resourceKind}?
      </DialogTitle>
      <DialogContent>
        <Stack spacing={2.5} sx={{ mt: 1 }}>
          <Typography variant="body2" color="text.secondary">
            This is destructive and irreversible. The Fly.io API will be
            called for each resource. Linked rows will break the runtime they
            point at.
          </Typography>

          {linkedItems.length > 0 && (
            <Box
              sx={{
                p: 1.5,
                borderRadius: 1,
                bgcolor: 'rgba(160, 62, 62, 0.08)',
                border: '1px solid rgba(160, 62, 62, 0.4)',
              }}
            >
              <Typography
                variant="caption"
                sx={{ color: '#A03E3E', fontWeight: 700, letterSpacing: 0.3 }}
              >
                LINKED ({linkedItems.length})
              </Typography>
              <Stack spacing={0.5} sx={{ mt: 0.5 }}>
                {linkedItems.map((it) => (
                  <Typography
                    key={it.id}
                    variant="body2"
                    sx={{ fontFamily: 'monospace', fontSize: '0.78rem' }}
                  >
                    <Box component="span" sx={{ color: '#A03E3E', fontWeight: 600 }}>
                      {it.name}
                    </Box>
                    {it.linkedProjectName || it.linkedBranchName ? (
                      <Box component="span" sx={{ color: 'text.secondary' }}>
                        {' — '}
                        {it.linkedProjectName ?? '?'}
                        {' / '}
                        {it.linkedBranchName ?? '?'}
                      </Box>
                    ) : null}
                  </Typography>
                ))}
              </Stack>
            </Box>
          )}

          {orphanItems.length > 0 && (
            <Box>
              <Typography
                variant="caption"
                color="text.secondary"
                sx={{ fontWeight: 700, letterSpacing: 0.3 }}
              >
                ORPHANS ({orphanItems.length})
              </Typography>
              <Stack spacing={0.5} sx={{ mt: 0.5 }}>
                {orphanItems.slice(0, 50).map((it) => (
                  <Typography
                    key={it.id}
                    variant="body2"
                    sx={{ fontFamily: 'monospace', fontSize: '0.78rem' }}
                  >
                    {it.name}
                  </Typography>
                ))}
                {orphanItems.length > 50 && (
                  <Typography variant="caption" color="text.secondary">
                    +{orphanItems.length - 50} more
                  </Typography>
                )}
              </Stack>
            </Box>
          )}

          {showForce && (
            <FormControlLabel
              control={
                <Switch
                  checked={force}
                  onChange={(e) => setForce(e.target.checked)}
                  disabled={isSubmitting}
                />
              }
              label="Force destroy (for stuck VMs)"
            />
          )}

          <TextField
            autoFocus
            size="small"
            fullWidth
            label="Type DELETE to confirm"
            placeholder="type DELETE to confirm"
            value={typed}
            onChange={(e) => setTyped(e.target.value)}
            disabled={isSubmitting}
            inputProps={{ 'aria-label': 'confirmation token' }}
          />

          {isSubmitting && (
            <Stack direction="row" spacing={1.5} alignItems="center">
              <CircularProgress size={16} />
              <Typography variant="caption" color="text.secondary">
                Destroying {items.length} {resourceKind} via Fly.io…
              </Typography>
            </Stack>
          )}

          {lastResult && lastResult.failed.length > 0 && (
            <Box>
              <Alert
                severity={lastResult.succeeded === 0 ? 'error' : 'warning'}
                action={
                  <IconButton
                    size="small"
                    onClick={() => setFailureExpanded((v) => !v)}
                    aria-label={
                      failureExpanded ? 'Collapse failures' : 'Expand failures'
                    }
                  >
                    {failureExpanded ? (
                      <ExpandLessIcon fontSize="small" />
                    ) : (
                      <ExpandMoreIcon fontSize="small" />
                    )}
                  </IconButton>
                }
              >
                {lastResult.succeeded} succeeded, {lastResult.failed.length} failed
              </Alert>
              <Collapse in={failureExpanded}>
                <Stack
                  spacing={0.5}
                  sx={{ mt: 1, pl: 2, maxHeight: 200, overflowY: 'auto' }}
                >
                  {lastResult.failed.map((f) => (
                    <Typography
                      key={f.id}
                      variant="caption"
                      sx={{ fontFamily: 'monospace' }}
                    >
                      <Box component="span" sx={{ color: 'text.primary' }}>
                        {f.id.slice(-12)}
                      </Box>
                      {' — '}
                      <Box component="span" sx={{ color: 'error.main' }}>
                        {f.error}
                      </Box>
                    </Typography>
                  ))}
                </Stack>
              </Collapse>
            </Box>
          )}
        </Stack>
      </DialogContent>
      <DialogActions>
        <Button onClick={handleClose} disabled={isSubmitting}>
          {lastResult ? 'Close' : 'Cancel'}
        </Button>
        <Button
          variant="contained"
          color="error"
          onClick={() => onConfirm(force)}
          disabled={!canSubmit}
        >
          {isSubmitting
            ? 'Destroying…'
            : `Destroy ${items.length} ${resourceKind}`}
        </Button>
      </DialogActions>
    </Dialog>
  )
}

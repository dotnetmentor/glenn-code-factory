import { useState } from 'react'
import { useParams } from 'react-router-dom'
import {
  Alert,
  Box,
  Button,
  CircularProgress,
  IconButton,
  Skeleton,
  Snackbar,
  Stack,
  Table,
  TableBody,
  TableCell,
  TableContainer,
  TableHead,
  TableRow,
  Tooltip,
  Typography,
} from '@mui/material'
import AddIcon from '@mui/icons-material/Add'
import DeleteOutlineIcon from '@mui/icons-material/DeleteOutline'
import EditIcon from '@mui/icons-material/Edit'
import VisibilityIcon from '@mui/icons-material/Visibility'
import { formatSwedishDateTime } from '@/applications/shared/utils'
import { useProjectSecrets } from '../hooks/useProjectSecrets'
import { AddVariableDialog } from '../components/AddVariableDialog'
import { EditVariableDialog } from '../components/EditVariableDialog'
import { RevealVariableDialog } from '../components/RevealVariableDialog'
import { DeleteVariableConfirm } from '../components/DeleteVariableConfirm'

const MASK = '\u2022'.repeat(8)

type SnackState = { open: boolean; msg: string; severity: 'success' | 'error' | 'info' }

export function EnvironmentVariablesPage() {
  const { projectId = '' } = useParams<{ projectId: string }>()
  const [snack, setSnack] = useState<SnackState>({ open: false, msg: '', severity: 'success' })

  const [addOpen, setAddOpen] = useState(false)
  const [editKey, setEditKey] = useState<string | null>(null)
  const [revealKey, setRevealKey] = useState<string | null>(null)
  const [deleteKey, setDeleteKey] = useState<string | null>(null)

  const notify = (msg: string, severity: SnackState['severity']) =>
    setSnack({ open: true, msg, severity })

  const {
    secrets,
    isLoading,
    isFetching,
    error,
    addSecret,
    updateSecret,
    deleteSecret,
    isAdding,
    isUpdating,
    isDeleting,
  } = useProjectSecrets({
    projectId,
    onSuccess: (msg) => notify(msg, 'success'),
    onError: (msg) => notify(msg, 'error'),
  })

  if (!projectId) {
    return <Alert severity="error">Missing project id in URL.</Alert>
  }

  const handleRevealClose = (reason?: 'auto' | 'manual' | 'error') => {
    setRevealKey(null)
    if (reason === 'auto') {
      notify('Value cleared from view', 'info')
    }
  }

  const existingKeys = secrets.map((s) => s.key)

  return (
    <>
      <Stack spacing={4}>
            <Box sx={{ display: 'flex', justifyContent: 'space-between', alignItems: 'flex-start', gap: 2 }}>
              <Box>
                <Typography variant="overline" color="text.secondary">
                  Project settings
                </Typography>
                <Typography variant="h4" component="h1" sx={{ mb: 0.5 }}>
                  Environment Variables
                </Typography>
                <Typography variant="body2" color="text.secondary">
                  Project {projectId}. Values are encrypted at rest and pushed to the runtime on save.
                </Typography>
              </Box>
              <Button
                variant="contained"
                startIcon={<AddIcon />}
                onClick={() => setAddOpen(true)}
                size="large"
                sx={{ flexShrink: 0 }}
              >
                Add variable
              </Button>
            </Box>

            {error && (
              <Alert severity="error">
                Failed to load variables.{' '}
                {error instanceof Error ? error.message : ''}
              </Alert>
            )}

            <TableContainer>
              <Table>
                <TableHead>
                  <TableRow>
                    <TableCell>Key</TableCell>
                    <TableCell>Value</TableCell>
                    <TableCell>Updated</TableCell>
                    <TableCell>By</TableCell>
                    <TableCell align="right" sx={{ width: 160 }}>
                      Actions
                    </TableCell>
                  </TableRow>
                </TableHead>
                <TableBody>
                  {isLoading &&
                    Array.from({ length: 3 }).map((_, i) => (
                      <TableRow key={`skel-${i}`}>
                        <TableCell>
                          <Skeleton width={140} />
                        </TableCell>
                        <TableCell>
                          <Skeleton width={100} />
                        </TableCell>
                        <TableCell>
                          <Skeleton width={140} />
                        </TableCell>
                        <TableCell>
                          <Skeleton width={100} />
                        </TableCell>
                        <TableCell align="right">
                          <Skeleton width={80} />
                        </TableCell>
                      </TableRow>
                    ))}

                  {!isLoading && secrets.length === 0 && !error && (
                    <TableRow>
                      <TableCell colSpan={5}>
                        <Box sx={{ textAlign: 'center', py: 6 }}>
                          <Typography variant="h6" color="text.secondary" gutterBottom>
                            No variables yet
                          </Typography>
                          <Typography variant="body2" color="text.secondary" sx={{ mb: 2 }}>
                            Add your first environment variable to make it available to the runtime.
                          </Typography>
                          <Button
                            variant="outlined"
                            startIcon={<AddIcon />}
                            onClick={() => setAddOpen(true)}
                          >
                            Add variable
                          </Button>
                        </Box>
                      </TableCell>
                    </TableRow>
                  )}

                  {!isLoading &&
                    secrets.map((s) => (
                      <TableRow key={s.key} hover>
                        <TableCell>
                          <Typography
                            variant="body2"
                            sx={{ fontFamily: 'monospace', fontWeight: 600 }}
                          >
                            {s.key}
                          </Typography>
                        </TableCell>
                        <TableCell>
                          <Typography
                            variant="body2"
                            sx={{ fontFamily: 'monospace', color: 'text.secondary' }}
                          >
                            {MASK}
                          </Typography>
                        </TableCell>
                        <TableCell>
                          <Typography variant="body2" color="text.secondary">
                            {formatSwedishDateTime(s.updatedAt) || '-'}
                          </Typography>
                        </TableCell>
                        <TableCell>
                          <Typography variant="body2" color="text.secondary">
                            {s.createdByUserName ?? '\u2014'}
                          </Typography>
                        </TableCell>
                        <TableCell align="right">
                          <Tooltip title="Show value">
                            <IconButton size="small" onClick={() => setRevealKey(s.key)}>
                              <VisibilityIcon fontSize="small" />
                            </IconButton>
                          </Tooltip>
                          <Tooltip title="Edit">
                            <IconButton size="small" onClick={() => setEditKey(s.key)}>
                              <EditIcon fontSize="small" />
                            </IconButton>
                          </Tooltip>
                          <Tooltip title="Delete">
                            <IconButton
                              size="small"
                              onClick={() => setDeleteKey(s.key)}
                              color="error"
                            >
                              <DeleteOutlineIcon fontSize="small" />
                            </IconButton>
                          </Tooltip>
                        </TableCell>
                      </TableRow>
                    ))}
                </TableBody>
              </Table>
            </TableContainer>

            {isFetching && !isLoading && (
              <Box sx={{ display: 'flex', justifyContent: 'center' }}>
                <CircularProgress size={16} />
              </Box>
            )}
          </Stack>

      <AddVariableDialog
        open={addOpen}
        onClose={() => setAddOpen(false)}
        onSubmit={addSecret}
        isSubmitting={isAdding}
        existingKeys={existingKeys}
      />

      <EditVariableDialog
        open={!!editKey}
        variableKey={editKey}
        onClose={() => setEditKey(null)}
        onSubmit={updateSecret}
        isSubmitting={isUpdating}
      />

      <RevealVariableDialog
        open={!!revealKey}
        projectId={projectId}
        variableKey={revealKey}
        onClose={handleRevealClose}
      />

      <DeleteVariableConfirm
        open={!!deleteKey}
        variableKey={deleteKey}
        onClose={() => setDeleteKey(null)}
        onConfirm={deleteSecret}
        isDeleting={isDeleting}
      />

      <Snackbar
        open={snack.open}
        autoHideDuration={2500}
        onClose={() => setSnack((s) => ({ ...s, open: false }))}
      >
        <Alert
          onClose={() => setSnack((s) => ({ ...s, open: false }))}
          severity={snack.severity}
          variant="filled"
          sx={{ width: '100%' }}
        >
          {snack.msg}
        </Alert>
      </Snackbar>
    </>
  )
}

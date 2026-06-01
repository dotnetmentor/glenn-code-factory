import { useState } from 'react'
import {
  Alert,
  Box,
  Button,
  Dialog,
  DialogActions,
  DialogContent,
  DialogContentText,
  DialogTitle,
  Snackbar,
  Stack,
  Typography,
  alpha,
} from '@mui/material'
import AddIcon from '@mui/icons-material/Add'
import RocketLaunchIcon from '@mui/icons-material/RocketLaunch'
import { useQueryClient } from '@tanstack/react-query'
import {
  getGetApiAdminProjectTemplatesQueryKey,
  useDeleteApiAdminProjectTemplatesTemplateId,
  useGetApiAdminProjectTemplates,
} from '../../../../../api/queries-commands'
import type { ProjectTemplateListItem } from '../../../../../api/queries-commands'
import { StartersTable } from '../components/StartersTable'
import { StarterEditorDialog } from '../components/StarterEditorDialog'
import type { StarterEditorMode } from '../components/StarterEditorDialog'

interface SnackState {
  open: boolean
  message: string
  severity: 'success' | 'error'
}

function extractErrorMessage(err: unknown): string {
  if (!err) return 'Unknown error'
  if (typeof err === 'string') return err
  if (typeof err === 'object') {
    const maybe = err as {
      message?: unknown
      title?: unknown
      detail?: unknown
      response?: { data?: unknown }
    }
    const data = maybe.response?.data
    if (data && typeof data === 'object') {
      const d = data as {
        detail?: unknown
        title?: unknown
        message?: unknown
      }
      if (typeof d.detail === 'string') return d.detail
      if (typeof d.title === 'string') return d.title
      if (typeof d.message === 'string') return d.message
    }
    if (typeof maybe.detail === 'string') return maybe.detail
    if (typeof maybe.title === 'string') return maybe.title
    if (typeof maybe.message === 'string') return maybe.message
  }
  return 'Something went wrong'
}

/**
 * Super-admin "Manage Starters" page. Lists every {@link ProjectTemplateListItem}
 * (including archived rows, dimmed) and exposes create / edit / archive
 * actions backed by the generated Orval hooks for {@code /api/admin/project-templates}.
 *
 * The page mirrors {@code AgentModelsPage} in spirit so the super-admin
 * surface reads as one product — same header rhythm, same calm chrome, same
 * row-action style.
 */
export function StartersPage() {
  const queryClient = useQueryClient()

  const [snack, setSnack] = useState<SnackState>({
    open: false,
    message: '',
    severity: 'success',
  })
  const [editorMode, setEditorMode] = useState<StarterEditorMode | null>(null)
  const [archiveCandidate, setArchiveCandidate] =
    useState<ProjectTemplateListItem | null>(null)

  const listQuery = useGetApiAdminProjectTemplates({
    query: { staleTime: 30_000 },
  })
  const rows = listQuery.data ?? []

  const showSnack = (message: string, severity: 'success' | 'error') =>
    setSnack({ open: true, message, severity })

  const archiveMutation = useDeleteApiAdminProjectTemplatesTemplateId({
    mutation: {
      onSuccess: () => {
        queryClient.invalidateQueries({
          queryKey: getGetApiAdminProjectTemplatesQueryKey(),
        })
      },
    },
  })

  const handleOpenCreate = () => setEditorMode({ kind: 'create' })
  const handleOpenEdit = (row: ProjectTemplateListItem) =>
    setEditorMode({ kind: 'edit', starter: row })

  const handleConfirmArchive = () => {
    const target = archiveCandidate
    if (!target) return
    archiveMutation.mutate(
      { templateId: target.id },
      {
        onSuccess: () => {
          showSnack(`Archived '${target.name}'.`, 'success')
          setArchiveCandidate(null)
        },
        onError: (err) => {
          showSnack(
            `Failed to archive: ${extractErrorMessage(err)}`,
            'error',
          )
          setArchiveCandidate(null)
        },
      },
    )
  }

  return (
    <>
      <Box sx={{ mb: 4 }}>
        <Box
          sx={{
            display: 'flex',
            alignItems: 'center',
            justifyContent: 'space-between',
            gap: 2,
            mb: 0.5,
            flexWrap: 'wrap',
          }}
        >
          <Box sx={{ display: 'flex', alignItems: 'center', gap: 1.5 }}>
            <Box
              sx={{
                width: 36,
                height: 36,
                borderRadius: 2,
                bgcolor: (theme) => alpha(theme.palette.primary.main, 0.06),
                border: '1px solid',
                borderColor: 'divider',
                display: 'flex',
                alignItems: 'center',
                justifyContent: 'center',
              }}
            >
              <RocketLaunchIcon
                sx={{ fontSize: 18, color: 'text.secondary' }}
              />
            </Box>
            <Box>
              <Typography variant="h4" component="h1" sx={{ lineHeight: 1.2 }}>
                Starters
              </Typography>
              <Typography variant="caption" color="text.secondary">
                Curated project templates that pair a GitHub template repo
                with an optional Runtime Spec.
              </Typography>
            </Box>
          </Box>
          <Button
            variant="contained"
            startIcon={<AddIcon />}
            onClick={handleOpenCreate}
            sx={{
              textTransform: 'none',
              boxShadow: 'none',
              '&:hover': { boxShadow: 'none' },
            }}
          >
            New Starter
          </Button>
        </Box>
      </Box>

      <Stack spacing={4}>
        <Box
          sx={{
            bgcolor: 'background.paper',
            borderRadius: 3,
            border: '1px solid',
            borderColor: 'divider',
            overflow: 'hidden',
            p: { xs: 2, sm: 3 },
          }}
        >
          <Box sx={{ mb: 2 }}>
            <Typography variant="h6" sx={{ fontSize: '1rem', fontWeight: 600 }}>
              All Starters
            </Typography>
            <Typography variant="caption" color="text.secondary">
              Archived rows stay in the catalogue but disappear from the
              user-facing picker.
            </Typography>
          </Box>
          {listQuery.isError ? (
            <Alert severity="error" sx={{ mb: 2 }}>
              {extractErrorMessage(listQuery.error)}
            </Alert>
          ) : null}
          <StartersTable
            rows={rows}
            loading={listQuery.isFetching}
            onEdit={handleOpenEdit}
            onArchive={setArchiveCandidate}
          />
        </Box>
      </Stack>

      <StarterEditorDialog
        open={editorMode !== null}
        onClose={() => setEditorMode(null)}
        mode={editorMode ?? { kind: 'create' }}
        onSaved={(message) => showSnack(message, 'success')}
        onError={(message) => showSnack(message, 'error')}
      />

      <Dialog
        open={!!archiveCandidate}
        onClose={
          archiveMutation.isPending
            ? undefined
            : () => setArchiveCandidate(null)
        }
        maxWidth="xs"
        fullWidth
      >
        <DialogTitle>Archive Starter?</DialogTitle>
        <DialogContent>
          <DialogContentText>
            The Starter is soft-deleted — projects already created from it are
            not affected, but the row disappears from the user-facing picker.
            It stays visible here (dimmed) so you can audit history.
          </DialogContentText>
          {archiveCandidate ? (
            <Typography
              variant="body2"
              sx={{
                mt: 2,
                fontFamily: '"SF Mono", "Fira Code", "Consolas", monospace',
                fontSize: '0.8rem',
                color: 'text.secondary',
              }}
            >
              {archiveCandidate.name} ({archiveCandidate.slug})
            </Typography>
          ) : null}
        </DialogContent>
        <DialogActions>
          <Button
            onClick={() => setArchiveCandidate(null)}
            disabled={archiveMutation.isPending}
            sx={{ textTransform: 'none' }}
          >
            Cancel
          </Button>
          <Button
            color="error"
            variant="contained"
            onClick={handleConfirmArchive}
            disabled={archiveMutation.isPending}
            sx={{
              textTransform: 'none',
              boxShadow: 'none',
              '&:hover': { boxShadow: 'none' },
            }}
          >
            {archiveMutation.isPending ? 'Archiving…' : 'Archive'}
          </Button>
        </DialogActions>
      </Dialog>

      <Snackbar
        open={snack.open}
        autoHideDuration={4000}
        onClose={() => setSnack((prev) => ({ ...prev, open: false }))}
        anchorOrigin={{ vertical: 'bottom', horizontal: 'center' }}
      >
        <Alert
          onClose={() => setSnack((prev) => ({ ...prev, open: false }))}
          severity={snack.severity}
          variant="filled"
          sx={{ borderRadius: 2, fontWeight: 500 }}
        >
          {snack.message}
        </Alert>
      </Snackbar>
    </>
  )
}

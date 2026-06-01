import { useState } from 'react'
import { useNavigate } from 'react-router-dom'
import {
  Alert,
  Box,
  Button,
  Chip,
  Dialog,
  DialogActions,
  DialogContent,
  DialogContentText,
  DialogTitle,
  IconButton,
  Snackbar,
  Stack,
  TextField,
  Tooltip,
  Typography,
  alpha,
} from '@mui/material'
import { DataGrid, type GridColDef } from '@mui/x-data-grid'
import AddIcon from '@mui/icons-material/Add'
import EditIcon from '@mui/icons-material/Edit'
import ContentCopyIcon from '@mui/icons-material/ContentCopy'
import DeleteOutlineIcon from '@mui/icons-material/DeleteOutline'
import LockIcon from '@mui/icons-material/Lock'
import SettingsApplicationsIcon from '@mui/icons-material/SettingsApplications'
import { useQueryClient } from '@tanstack/react-query'
import {
  getGetApiAdminRuntimePresetsQueryKey,
  useDeleteApiAdminRuntimePresetsId,
  useGetApiAdminRuntimePresets,
  usePostApiAdminRuntimePresetsIdClone,
  type ServicePresetDto,
} from '@/api/queries-commands'

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

const SLUG_PATTERN = /^[a-z][a-z0-9-]+$/

/**
 * Super-admin Runtime Presets list page. Renders every {@link ServicePresetDto}
 * with category badge, built-in lock icon, last-updated timestamp, and
 * row-level Edit / Clone / Delete actions.
 *
 * <p>Built-in presets are clone-only (matching the V3 spec: seed presets
 * stay pristine, operators clone them to customise). Delete also rejects
 * built-ins with a 409 from the backend; we hide the button rather than
 * letting the error round-trip.</p>
 */
export function RuntimePresetsListPage() {
  const navigate = useNavigate()
  const queryClient = useQueryClient()

  const [snack, setSnack] = useState<SnackState>({
    open: false,
    message: '',
    severity: 'success',
  })

  const [cloneTarget, setCloneTarget] = useState<ServicePresetDto | null>(null)
  const [cloneSlug, setCloneSlug] = useState('')
  const [cloneDisplayName, setCloneDisplayName] = useState('')
  const [cloneError, setCloneError] = useState<string | null>(null)

  const [deleteTarget, setDeleteTarget] = useState<ServicePresetDto | null>(
    null,
  )

  const listQuery = useGetApiAdminRuntimePresets({
    query: { staleTime: 30_000 },
  })
  const rows = listQuery.data ?? []

  const showSnack = (message: string, severity: 'success' | 'error') =>
    setSnack({ open: true, message, severity })

  const cloneMutation = usePostApiAdminRuntimePresetsIdClone()
  const deleteMutation = useDeleteApiAdminRuntimePresetsId()

  const invalidate = () => {
    queryClient.invalidateQueries({
      queryKey: getGetApiAdminRuntimePresetsQueryKey(),
    })
  }

  const handleCloneOpen = (preset: ServicePresetDto) => {
    setCloneTarget(preset)
    setCloneSlug(`${preset.slug}-copy`)
    setCloneDisplayName(`${preset.displayName} (copy)`)
    setCloneError(null)
  }

  const handleCloneSubmit = () => {
    if (!cloneTarget) return
    const slug = cloneSlug.trim()
    if (!SLUG_PATTERN.test(slug)) {
      setCloneError(
        'Slug must start with a lowercase letter and contain only lowercase letters, digits, and hyphens.',
      )
      return
    }
    setCloneError(null)
    cloneMutation.mutate(
      {
        id: cloneTarget.id,
        data: {
          newSlug: slug,
          newDisplayName: cloneDisplayName.trim() || null,
        },
      },
      {
        onSuccess: (created) => {
          invalidate()
          setCloneTarget(null)
          showSnack(`Cloned '${cloneTarget.slug}' to '${created.slug}'.`, 'success')
          navigate(`/super-admin/runtime-presets/${created.id}`)
        },
        onError: (err) => {
          setCloneError(extractErrorMessage(err))
        },
      },
    )
  }

  const handleDeleteConfirm = () => {
    if (!deleteTarget) return
    deleteMutation.mutate(
      { id: deleteTarget.id },
      {
        onSuccess: () => {
          invalidate()
          showSnack(`Deleted '${deleteTarget.slug}'.`, 'success')
          setDeleteTarget(null)
        },
        onError: (err) => {
          showSnack(
            `Failed to delete: ${extractErrorMessage(err)}`,
            'error',
          )
          setDeleteTarget(null)
        },
      },
    )
  }

  const columns: GridColDef<ServicePresetDto>[] = [
    {
      field: 'isBuiltIn',
      headerName: '',
      width: 50,
      sortable: false,
      filterable: false,
      renderCell: (p) =>
        p.value ? (
          <Tooltip title="Built-in preset (clone to customize)">
            <LockIcon sx={{ fontSize: 16, color: 'text.secondary' }} />
          </Tooltip>
        ) : null,
    },
    {
      field: 'slug',
      headerName: 'Slug',
      flex: 1,
      minWidth: 180,
      renderCell: (p) => (
        <Typography
          variant="body2"
          sx={{
            fontFamily: 'monospace',
            fontSize: '0.8rem',
            color: 'text.primary',
          }}
        >
          {p.value as string}
        </Typography>
      ),
    },
    {
      field: 'displayName',
      headerName: 'Display Name',
      flex: 1.3,
      minWidth: 200,
    },
    {
      field: 'category',
      headerName: 'Category',
      width: 130,
      renderCell: (p) => (
        <Chip
          size="small"
          label={(p.value as string) || 'Other'}
          variant="outlined"
          sx={{ fontWeight: 500 }}
        />
      ),
    },
    {
      field: 'description',
      headerName: 'Description',
      flex: 1.5,
      minWidth: 220,
      sortable: false,
      renderCell: (p) => (
        <Tooltip title={p.value as string} placement="top" arrow>
          <Typography
            variant="body2"
            sx={{
              fontSize: '0.8rem',
              color: 'text.secondary',
              overflow: 'hidden',
              textOverflow: 'ellipsis',
              whiteSpace: 'nowrap',
            }}
          >
            {p.value as string}
          </Typography>
        </Tooltip>
      ),
    },
    {
      field: 'updatedAt',
      headerName: 'Updated',
      width: 150,
      renderCell: (p) => {
        const raw = p.value as string | undefined
        if (!raw) return '—'
        const d = new Date(raw)
        return Number.isNaN(d.getTime())
          ? raw
          : d.toLocaleDateString(undefined, {
              year: 'numeric',
              month: 'short',
              day: '2-digit',
            })
      },
    },
    {
      field: 'actions',
      headerName: 'Actions',
      width: 160,
      sortable: false,
      filterable: false,
      renderCell: (p) => {
        const row = p.row
        return (
          <Stack direction="row" spacing={0.5} sx={{ alignItems: 'center' }}>
            <Tooltip title={row.isBuiltIn ? 'Built-in — clone to edit' : 'Edit'}>
              <span>
                <IconButton
                  size="small"
                  onClick={() =>
                    navigate(`/super-admin/runtime-presets/${row.id}`)
                  }
                  disabled={row.isBuiltIn}
                  aria-label="Edit preset"
                >
                  <EditIcon sx={{ fontSize: 18 }} />
                </IconButton>
              </span>
            </Tooltip>
            <Tooltip title="Clone">
              <IconButton
                size="small"
                onClick={() => handleCloneOpen(row)}
                aria-label="Clone preset"
              >
                <ContentCopyIcon sx={{ fontSize: 18 }} />
              </IconButton>
            </Tooltip>
            <Tooltip title={row.isBuiltIn ? 'Built-in — cannot delete' : 'Delete'}>
              <span>
                <IconButton
                  size="small"
                  color="error"
                  onClick={() => setDeleteTarget(row)}
                  disabled={row.isBuiltIn}
                  aria-label="Delete preset"
                >
                  <DeleteOutlineIcon sx={{ fontSize: 18 }} />
                </IconButton>
              </span>
            </Tooltip>
          </Stack>
        )
      },
    },
  ]

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
              <SettingsApplicationsIcon
                sx={{ fontSize: 18, color: 'text.secondary' }}
              />
            </Box>
            <Box>
              <Typography variant="h4" component="h1" sx={{ lineHeight: 1.2 }}>
                Runtime Presets
              </Typography>
              <Typography variant="caption" color="text.secondary">
                Service templates the agent picks from when proposing a
                runtime spec. Built-ins are clone-only.
              </Typography>
            </Box>
          </Box>
          <Button
            variant="contained"
            startIcon={<AddIcon />}
            onClick={() => navigate('/super-admin/runtime-presets/new')}
            sx={{
              textTransform: 'none',
              boxShadow: 'none',
              '&:hover': { boxShadow: 'none' },
            }}
          >
            New Preset
          </Button>
        </Box>
      </Box>

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
        {listQuery.isError && (
          <Alert severity="error" sx={{ mb: 2 }}>
            {extractErrorMessage(listQuery.error)}
          </Alert>
        )}

        <Box sx={{ width: '100%' }}>
          <DataGrid
            autoHeight
            rows={rows}
            columns={columns}
            loading={listQuery.isFetching}
            getRowId={(row) => row.id}
            disableRowSelectionOnClick
            disableColumnMenu
            pageSizeOptions={[10, 25, 50]}
            initialState={{
              pagination: { paginationModel: { pageSize: 25, page: 0 } },
              sorting: { sortModel: [{ field: 'slug', sort: 'asc' }] },
            }}
            slots={{
              noRowsOverlay: () => (
                <Stack
                  alignItems="center"
                  justifyContent="center"
                  spacing={1}
                  sx={{ height: '100%', p: 4 }}
                >
                  <Typography variant="body2" color="text.secondary">
                    No presets yet.
                  </Typography>
                  <Typography variant="caption" color="text.secondary">
                    Built-in seeds are added by the migration. If you don't
                    see them, the migration may not have run yet.
                  </Typography>
                </Stack>
              ),
            }}
            sx={{
              border: 'none',
              '& .MuiDataGrid-cell:focus, & .MuiDataGrid-cell:focus-within': {
                outline: 'none',
              },
              '& .MuiDataGrid-columnHeader:focus, & .MuiDataGrid-columnHeader:focus-within':
                {
                  outline: 'none',
                },
            }}
          />
        </Box>
      </Box>

      <Dialog
        open={!!cloneTarget}
        onClose={
          cloneMutation.isPending ? undefined : () => setCloneTarget(null)
        }
        maxWidth="xs"
        fullWidth
      >
        <DialogTitle>Clone preset</DialogTitle>
        <DialogContent>
          <DialogContentText sx={{ mb: 2 }}>
            Creates a writable copy of <strong>{cloneTarget?.slug}</strong>.
            Pick a new slug — must start with a lowercase letter and contain
            only lowercase letters, digits, and hyphens.
          </DialogContentText>
          <Stack spacing={2}>
            <TextField
              label="New slug"
              value={cloneSlug}
              onChange={(e) => setCloneSlug(e.target.value)}
              size="small"
              fullWidth
              required
              autoFocus
              slotProps={{
                input: { sx: { fontFamily: 'monospace', fontSize: 13 } },
              }}
            />
            <TextField
              label="New display name (optional)"
              value={cloneDisplayName}
              onChange={(e) => setCloneDisplayName(e.target.value)}
              size="small"
              fullWidth
            />
            {cloneError && <Alert severity="error">{cloneError}</Alert>}
          </Stack>
        </DialogContent>
        <DialogActions>
          <Button
            onClick={() => setCloneTarget(null)}
            disabled={cloneMutation.isPending}
            sx={{ textTransform: 'none' }}
          >
            Cancel
          </Button>
          <Button
            variant="contained"
            onClick={handleCloneSubmit}
            disabled={cloneMutation.isPending}
            sx={{
              textTransform: 'none',
              boxShadow: 'none',
              '&:hover': { boxShadow: 'none' },
            }}
          >
            {cloneMutation.isPending ? 'Cloning…' : 'Clone'}
          </Button>
        </DialogActions>
      </Dialog>

      <Dialog
        open={!!deleteTarget}
        onClose={
          deleteMutation.isPending ? undefined : () => setDeleteTarget(null)
        }
        maxWidth="xs"
        fullWidth
      >
        <DialogTitle>Delete preset?</DialogTitle>
        <DialogContent>
          <DialogContentText>
            This soft-deletes the preset. Projects that have already proposed
            specs using <strong>{deleteTarget?.slug}</strong> will keep
            working — but new proposals can't reference this kind anymore.
          </DialogContentText>
        </DialogContent>
        <DialogActions>
          <Button
            onClick={() => setDeleteTarget(null)}
            disabled={deleteMutation.isPending}
            sx={{ textTransform: 'none' }}
          >
            Cancel
          </Button>
          <Button
            color="error"
            variant="contained"
            onClick={handleDeleteConfirm}
            disabled={deleteMutation.isPending}
            sx={{
              textTransform: 'none',
              boxShadow: 'none',
              '&:hover': { boxShadow: 'none' },
            }}
          >
            {deleteMutation.isPending ? 'Deleting…' : 'Delete'}
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

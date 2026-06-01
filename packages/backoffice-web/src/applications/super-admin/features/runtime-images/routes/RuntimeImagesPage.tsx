import { useMemo, useState } from 'react'
import {
  Alert,
  AlertTitle,
  Box,
  Button,
  Dialog,
  DialogActions,
  DialogContent,
  DialogTitle,
  Snackbar,
  Stack,
  TextField,
  Typography,
  alpha,
} from '@mui/material'
import RefreshIcon from '@mui/icons-material/Refresh'
import MemoryIcon from '@mui/icons-material/Memory'
import { useQueryClient } from '@tanstack/react-query'
import {
  RegistryTagDto,
  RuntimeImage,
  RuntimeImageStatus,
  getGetApiAdminRuntimeImagesQueryKey,
  getGetApiAdminRuntimeImagesRegistryTagsQueryKey,
  useGetApiAdminRuntimeImages,
  useGetApiAdminRuntimeImagesRegistryTags,
  usePatchApiAdminRuntimeImagesIdStatus,
  usePostApiAdminRuntimeImages,
} from '../../../../../api/queries-commands'
import { RegisteredImagesTable } from '../components/RegisteredImagesTable'
import { RegistryTagsTable } from '../components/RegistryTagsTable'

const REGISTRY = 'registry.fly.io/glenn-runtime-base'

interface SnackState {
  open: boolean
  message: string
  severity: 'success' | 'error'
}

interface RegisterDialogState {
  open: boolean
  tag: RegistryTagDto | null
  notes: string
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
      const dataObj = data as { detail?: unknown; title?: unknown; message?: unknown }
      if (typeof dataObj.detail === 'string') return dataObj.detail
      if (typeof dataObj.title === 'string') return dataObj.title
      if (typeof dataObj.message === 'string') return dataObj.message
    }
    if (typeof maybe.detail === 'string') return maybe.detail
    if (typeof maybe.title === 'string') return maybe.title
    if (typeof maybe.message === 'string') return maybe.message
  }
  return 'Something went wrong'
}

export function RuntimeImagesPage() {
  const queryClient = useQueryClient()

  const [snack, setSnack] = useState<SnackState>({ open: false, message: '', severity: 'success' })
  const [registerDialog, setRegisterDialog] = useState<RegisterDialogState>({
    open: false,
    tag: null,
    notes: '',
  })
  const [pendingActionId, setPendingActionId] = useState<string | null>(null)

  const registeredQuery = useGetApiAdminRuntimeImages(undefined, {
    query: { staleTime: 30_000 },
  })
  const registryQuery = useGetApiAdminRuntimeImagesRegistryTags(undefined, {
    query: { refetchOnWindowFocus: false, staleTime: 60_000 },
  })

  const registeredImages = registeredQuery.data?.items ?? []
  const registryTags = registryQuery.data ?? []

  const registeredTagSet = useMemo(
    () => new Set(registeredImages.map((img) => img.tag)),
    [registeredImages],
  )

  const showSnack = (message: string, severity: 'success' | 'error') =>
    setSnack({ open: true, message, severity })

  const invalidateRegistered = () =>
    queryClient.invalidateQueries({ queryKey: getGetApiAdminRuntimeImagesQueryKey() })
  const invalidateRegistry = () =>
    queryClient.invalidateQueries({
      queryKey: getGetApiAdminRuntimeImagesRegistryTagsQueryKey(),
    })

  const statusMutation = usePatchApiAdminRuntimeImagesIdStatus({
    mutation: {
      onSuccess: () => {
        invalidateRegistered()
      },
      onSettled: () => {
        setPendingActionId(null)
      },
    },
  })

  const registerMutation = usePostApiAdminRuntimeImages({
    mutation: {
      onSuccess: () => {
        invalidateRegistered()
        invalidateRegistry()
      },
    },
  })

  const handleStatusChange = (image: RuntimeImage, status: RuntimeImageStatus, label: string) => {
    setPendingActionId(image.id)
    statusMutation.mutate(
      { id: image.id, data: { status } },
      {
        onSuccess: () => {
          showSnack(`Image ${label}: ${image.tag}`, 'success')
        },
        onError: (err) => {
          showSnack(`Failed to ${label.toLowerCase()}: ${extractErrorMessage(err)}`, 'error')
        },
      },
    )
  }

  const handleActivate = (image: RuntimeImage) => handleStatusChange(image, 'Active', 'activated')
  const handleDeprecate = (image: RuntimeImage) =>
    handleStatusChange(image, 'Deprecated', 'deprecated')
  const handleYank = (image: RuntimeImage) => handleStatusChange(image, 'Yanked', 'yanked')

  const handleOpenRegister = (tag: RegistryTagDto) => {
    setRegisterDialog({ open: true, tag, notes: '' })
  }

  const handleCloseRegister = () => {
    setRegisterDialog({ open: false, tag: null, notes: '' })
  }

  const handleSubmitRegister = () => {
    const tag = registerDialog.tag
    if (!tag) return
    const sizeMb = tag.sizeBytes != null ? Math.round(tag.sizeBytes / 1024 / 1024) : 0
    registerMutation.mutate(
      {
        data: {
          tag: tag.tag,
          digest: tag.digest,
          registry: REGISTRY,
          gitSha: tag.gitSha ?? '',
          builtAt: tag.pushedAt ?? new Date().toISOString(),
          sizeMb,
          notes: registerDialog.notes.trim() || null,
        },
      },
      {
        onSuccess: () => {
          showSnack(`Registered ${tag.tag}`, 'success')
          handleCloseRegister()
        },
        onError: (err) => {
          showSnack(`Failed to register: ${extractErrorMessage(err)}`, 'error')
        },
      },
    )
  }

  const registryError = registryQuery.error
    ? extractErrorMessage(registryQuery.error)
    : null

  return (
    <>
      <Box sx={{ mb: 4 }}>
        <Box sx={{ display: 'flex', alignItems: 'center', gap: 1.5, mb: 0.5 }}>
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
            <MemoryIcon sx={{ fontSize: 18, color: 'text.secondary' }} />
          </Box>
          <Box>
            <Typography variant="h4" component="h1" sx={{ lineHeight: 1.2 }}>
              Runtime Images
            </Typography>
            <Typography variant="caption" color="text.secondary">
              Register Fly registry tags and choose which one is active
            </Typography>
          </Box>
        </Box>
      </Box>

      <Stack spacing={4}>
        {/* Section 1: Registered images (DB) */}
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
              Registered images
            </Typography>
            <Typography variant="caption" color="text.secondary">
              Images known to the runtime provisioner. The Active row is what new Machines use.
            </Typography>
          </Box>
          <RegisteredImagesTable
            rows={registeredImages}
            loading={registeredQuery.isFetching}
            pendingActionId={pendingActionId}
            onActivate={handleActivate}
            onDeprecate={handleDeprecate}
            onYank={handleYank}
          />
        </Box>

        {/* Section 2: Available in Fly registry */}
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
          <Box
            sx={{
              display: 'flex',
              alignItems: 'flex-start',
              justifyContent: 'space-between',
              mb: 2,
              gap: 2,
              flexWrap: 'wrap',
            }}
          >
            <Box>
              <Typography variant="h6" sx={{ fontSize: '1rem', fontWeight: 600 }}>
                Available in Fly registry
              </Typography>
              <Typography variant="caption" color="text.secondary">
                Live tags pushed to{' '}
                <Box
                  component="span"
                  sx={{
                    fontFamily: '"SF Mono", "Fira Code", "Consolas", monospace',
                    fontSize: '0.75rem',
                  }}
                >
                  {REGISTRY}
                </Box>
                . Refreshes only on demand.
              </Typography>
            </Box>
            <Button
              size="small"
              variant="outlined"
              startIcon={<RefreshIcon />}
              onClick={() => registryQuery.refetch()}
              disabled={registryQuery.isFetching}
              sx={{ textTransform: 'none', fontWeight: 500 }}
            >
              {registryQuery.isFetching ? 'Refreshing...' : 'Refresh from Fly'}
            </Button>
          </Box>

          {registryError ? (
            <Alert
              severity="error"
              sx={{ mb: 2 }}
              action={
                <Button color="inherit" size="small" onClick={() => registryQuery.refetch()}>
                  Retry
                </Button>
              }
            >
              <AlertTitle>Couldn't reach Fly registry</AlertTitle>
              {registryError}
            </Alert>
          ) : null}

          <RegistryTagsTable
            rows={registryTags}
            loading={registryQuery.isFetching}
            registeredTagSet={registeredTagSet}
            onRegister={handleOpenRegister}
          />
        </Box>
      </Stack>

      {/* Register dialog */}
      <Dialog
        open={registerDialog.open}
        onClose={registerMutation.isPending ? undefined : handleCloseRegister}
        maxWidth="sm"
        fullWidth
      >
        <DialogTitle>Register image</DialogTitle>
        <DialogContent>
          {registerDialog.tag ? (
            <Stack spacing={2} sx={{ mt: 1 }}>
              <Box>
                <Typography
                  variant="overline"
                  sx={{ fontSize: '0.65rem', color: 'text.disabled', letterSpacing: '0.08em' }}
                >
                  Tag
                </Typography>
                <Typography
                  variant="body2"
                  sx={{
                    fontFamily: '"SF Mono", "Fira Code", "Consolas", monospace',
                    fontSize: '0.825rem',
                  }}
                >
                  {registerDialog.tag.tag}
                </Typography>
              </Box>
              <Box>
                <Typography
                  variant="overline"
                  sx={{ fontSize: '0.65rem', color: 'text.disabled', letterSpacing: '0.08em' }}
                >
                  Digest
                </Typography>
                <Typography
                  variant="body2"
                  sx={{
                    fontFamily: '"SF Mono", "Fira Code", "Consolas", monospace',
                    fontSize: '0.775rem',
                    wordBreak: 'break-all',
                  }}
                >
                  {registerDialog.tag.digest}
                </Typography>
              </Box>
              <Box>
                <Typography
                  variant="overline"
                  sx={{ fontSize: '0.65rem', color: 'text.disabled', letterSpacing: '0.08em' }}
                >
                  Size
                </Typography>
                <Typography variant="body2">
                  {registerDialog.tag.sizeBytes != null
                    ? `${Math.round(registerDialog.tag.sizeBytes / 1024 / 1024)} MB`
                    : '\u2014'}
                </Typography>
              </Box>
              <TextField
                label="Notes (optional)"
                value={registerDialog.notes}
                onChange={(e) =>
                  setRegisterDialog((prev) => ({ ...prev, notes: e.target.value }))
                }
                multiline
                minRows={2}
                fullWidth
                disabled={registerMutation.isPending}
              />
            </Stack>
          ) : null}
        </DialogContent>
        <DialogActions>
          <Button
            onClick={handleCloseRegister}
            disabled={registerMutation.isPending}
            sx={{ textTransform: 'none' }}
          >
            Cancel
          </Button>
          <Button
            variant="contained"
            onClick={handleSubmitRegister}
            disabled={registerMutation.isPending || !registerDialog.tag}
            sx={{ textTransform: 'none', boxShadow: 'none', '&:hover': { boxShadow: 'none' } }}
          >
            {registerMutation.isPending ? 'Registering...' : 'Register'}
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

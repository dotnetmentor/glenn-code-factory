import { useState } from 'react'
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
  RuntimeTemplate,
  RuntimeTemplateStatus,
  TemplateCandidateBoxDto,
  getGetApiAdminRuntimeTemplatesBoxesQueryKey,
  getGetApiAdminRuntimeTemplatesQueryKey,
  useGetApiAdminRuntimeTemplates,
  useGetApiAdminRuntimeTemplatesBoxes,
  usePatchApiAdminRuntimeTemplatesIdStatus,
  usePostApiAdminRuntimeTemplates,
} from '../../../../../api/queries-commands'
import { RegisteredTemplatesTable } from '../components/RegisteredTemplatesTable'
import { CandidateBoxesTable } from '../components/CandidateBoxesTable'

interface SnackState {
  open: boolean
  message: string
  severity: 'success' | 'error'
}

interface RegisterDialogState {
  open: boolean
  box: TemplateCandidateBoxDto | null
  label: string
  gitSha: string
  notes: string
}

const CLOSED_REGISTER_DIALOG: RegisterDialogState = {
  open: false,
  box: null,
  label: '',
  gitSha: '',
  notes: '',
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

export function RuntimeTemplatesPage() {
  const queryClient = useQueryClient()

  const [snack, setSnack] = useState<SnackState>({ open: false, message: '', severity: 'success' })
  const [registerDialog, setRegisterDialog] = useState<RegisterDialogState>(CLOSED_REGISTER_DIALOG)
  const [pendingActionId, setPendingActionId] = useState<string | null>(null)

  const registeredQuery = useGetApiAdminRuntimeTemplates(undefined, {
    query: { staleTime: 30_000 },
  })
  const candidatesQuery = useGetApiAdminRuntimeTemplatesBoxes({
    query: { refetchOnWindowFocus: false, staleTime: 60_000 },
  })

  const registeredTemplates = registeredQuery.data?.items ?? []
  const candidateBoxes = candidatesQuery.data ?? []

  const showSnack = (message: string, severity: 'success' | 'error') =>
    setSnack({ open: true, message, severity })

  const invalidateRegistered = () =>
    queryClient.invalidateQueries({ queryKey: getGetApiAdminRuntimeTemplatesQueryKey() })
  const invalidateCandidates = () =>
    queryClient.invalidateQueries({
      queryKey: getGetApiAdminRuntimeTemplatesBoxesQueryKey(),
    })

  const statusMutation = usePatchApiAdminRuntimeTemplatesIdStatus({
    mutation: {
      onSuccess: () => {
        invalidateRegistered()
      },
      onSettled: () => {
        setPendingActionId(null)
      },
    },
  })

  const registerMutation = usePostApiAdminRuntimeTemplates({
    mutation: {
      onSuccess: () => {
        invalidateRegistered()
        invalidateCandidates()
      },
    },
  })

  const handleStatusChange = (
    template: RuntimeTemplate,
    status: RuntimeTemplateStatus,
    label: string,
  ) => {
    setPendingActionId(template.id)
    statusMutation.mutate(
      { id: template.id, data: { status } },
      {
        onSuccess: () => {
          showSnack(`Template ${label}: ${template.label}`, 'success')
        },
        onError: (err) => {
          showSnack(`Failed to ${label.toLowerCase()}: ${extractErrorMessage(err)}`, 'error')
        },
      },
    )
  }

  const handleActivate = (template: RuntimeTemplate) =>
    handleStatusChange(template, 'Active', 'activated')
  const handleDeprecate = (template: RuntimeTemplate) =>
    handleStatusChange(template, 'Deprecated', 'deprecated')
  const handleYank = (template: RuntimeTemplate) => handleStatusChange(template, 'Yanked', 'yanked')

  const handleOpenRegister = (box: TemplateCandidateBoxDto) => {
    setRegisterDialog({
      ...CLOSED_REGISTER_DIALOG,
      open: true,
      box,
      // Pre-fill the label with the box name — usually already descriptive
      // (build-box-template.sh names boxes after the build).
      label: box.name ?? '',
    })
  }

  const handleCloseRegister = () => {
    setRegisterDialog(CLOSED_REGISTER_DIALOG)
  }

  const handleSubmitRegister = () => {
    const box = registerDialog.box
    const label = registerDialog.label.trim()
    if (!box || !label) return
    registerMutation.mutate(
      {
        data: {
          boxId: box.id,
          label,
          gitSha: registerDialog.gitSha.trim(),
          builtAt: new Date().toISOString(),
          notes: registerDialog.notes.trim() || null,
        },
      },
      {
        onSuccess: () => {
          showSnack(`Registered ${label}`, 'success')
          handleCloseRegister()
        },
        onError: (err) => {
          showSnack(`Failed to register: ${extractErrorMessage(err)}`, 'error')
        },
      },
    )
  }

  const candidatesError = candidatesQuery.error
    ? extractErrorMessage(candidatesQuery.error)
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
              Runtime Templates
            </Typography>
            <Typography variant="caption" color="text.secondary">
              Register golden boxes and choose which one new runtimes fork from
            </Typography>
          </Box>
        </Box>
      </Box>

      <Stack spacing={4}>
        {/* Section 1: Registered templates (DB) */}
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
              Registered templates
            </Typography>
            <Typography variant="caption" color="text.secondary">
              Golden boxes known to the runtime provisioner. The newest Active row is the
              fork source for every new runtime.
            </Typography>
          </Box>
          <RegisteredTemplatesTable
            rows={registeredTemplates}
            loading={registeredQuery.isFetching}
            pendingActionId={pendingActionId}
            onActivate={handleActivate}
            onDeprecate={handleDeprecate}
            onYank={handleYank}
          />
        </Box>

        {/* Section 2: Template candidates (live boxes on the Box account) */}
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
                Template candidates
              </Typography>
              <Typography variant="caption" color="text.secondary">
                Live boxes on the Box account — golden templates built by{' '}
                <Box
                  component="span"
                  sx={{
                    fontFamily: '"SF Mono", "Fira Code", "Consolas", monospace',
                    fontSize: '0.75rem',
                  }}
                >
                  scripts/build-box-template.sh
                </Box>
                . Refreshes only on demand.
              </Typography>
            </Box>
            <Button
              size="small"
              variant="outlined"
              startIcon={<RefreshIcon />}
              onClick={() => candidatesQuery.refetch()}
              disabled={candidatesQuery.isFetching}
              sx={{ textTransform: 'none', fontWeight: 500 }}
            >
              {candidatesQuery.isFetching ? 'Refreshing...' : 'Refresh from Box'}
            </Button>
          </Box>

          {candidatesError ? (
            <Alert
              severity="error"
              sx={{ mb: 2 }}
              action={
                <Button color="inherit" size="small" onClick={() => candidatesQuery.refetch()}>
                  Retry
                </Button>
              }
            >
              <AlertTitle>Couldn't reach the Box API</AlertTitle>
              {candidatesError}
            </Alert>
          ) : null}

          <CandidateBoxesTable
            rows={candidateBoxes}
            loading={candidatesQuery.isFetching}
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
        <DialogTitle>Register template</DialogTitle>
        <DialogContent>
          {registerDialog.box ? (
            <Stack spacing={2} sx={{ mt: 1 }}>
              <Box>
                <Typography
                  variant="overline"
                  sx={{ fontSize: '0.65rem', color: 'text.disabled', letterSpacing: '0.08em' }}
                >
                  Box
                </Typography>
                <Typography
                  variant="body2"
                  sx={{
                    fontFamily: '"SF Mono", "Fira Code", "Consolas", monospace',
                    fontSize: '0.825rem',
                  }}
                >
                  {registerDialog.box.id}
                  {registerDialog.box.name ? ` (${registerDialog.box.name})` : ''}
                </Typography>
              </Box>
              <TextField
                label="Label"
                required
                value={registerDialog.label}
                onChange={(e) =>
                  setRegisterDialog((prev) => ({ ...prev, label: e.target.value }))
                }
                fullWidth
                disabled={registerMutation.isPending}
              />
              <TextField
                label="Git SHA (optional)"
                value={registerDialog.gitSha}
                onChange={(e) =>
                  setRegisterDialog((prev) => ({ ...prev, gitSha: e.target.value }))
                }
                fullWidth
                disabled={registerMutation.isPending}
                slotProps={{
                  input: {
                    sx: {
                      fontFamily: '"SF Mono", "Fira Code", "Consolas", monospace',
                      fontSize: '0.825rem',
                    },
                  },
                }}
              />
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
            disabled={
              registerMutation.isPending ||
              !registerDialog.box ||
              !registerDialog.label.trim()
            }
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

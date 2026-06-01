import { useState } from 'react'
import { useNavigate } from 'react-router-dom'
import {
  Alert,
  Box,
  Button,
  CircularProgress,
  Container,
  TextField,
  Typography,
} from '@mui/material'
import ArrowBackIcon from '@mui/icons-material/ArrowBack'
import { usePostApiAdminRuntimes } from '@/api/queries-commands'
import {
  StackPickerFields,
  type StackPickerValue,
} from '../components/StackPickerFields'

/**
 * Manual onboarding flow: user picks languages, services and free-text extras
 * up front. We POST the spec alongside the runtime-create call so the
 * provisioner brings the runtime up with the picked stack from day one
 * (Spec 16, Scene 1).
 *
 * Form fields are factored into {@link StackPickerFields} so Card 8's
 * "Edit proposal" surface can reuse the same inputs against a different
 * submit handler.
 */
export function ManualStackPicker() {
  const navigate = useNavigate()
  const [name, setName] = useState('')
  const [stack, setStack] = useState<StackPickerValue>({
    languages: [],
    services: [],
    extras: [],
  })
  const [extrasRaw, setExtrasRaw] = useState('')
  const [serverError, setServerError] = useState<string | null>(null)

  const createMutation = usePostApiAdminRuntimes({
    mutation: {
      onError: (err: unknown) => {
        const msg =
          err instanceof Error
            ? err.message
            : 'Failed to create runtime. Please try again.'
        setServerError(msg)
      },
      onSuccess: (data) => {
        // Spec 16 Card 8 added the runtime workspace at this path; that's the
        // canonical landing page after onboarding.
        navigate(`/super-admin/projects/${data.projectId}/runtime`)
      },
    },
  })

  const canSubmit = name.trim().length > 0 && !createMutation.isPending

  const handleSubmit = (e: React.FormEvent) => {
    e.preventDefault()
    if (!canSubmit) return
    setServerError(null)
    createMutation.mutate({
      data: {
        name: name.trim(),
        initialSpec: {
          languages: stack.languages,
          services: stack.services,
          extras: stack.extras,
        },
      },
    })
  }

  return (
    <Container maxWidth="sm" sx={{ py: 6 }}>
      <Button
        startIcon={<ArrowBackIcon />}
        onClick={() => navigate('/super-admin/projects/new')}
        sx={{ mb: 2 }}
      >
        Back
      </Button>

      <Box component="form" onSubmit={handleSubmit} noValidate>
            <Typography variant="overline" color="text.secondary">
              New project
            </Typography>
            <Typography variant="h4" component="h1" sx={{ mb: 3 }}>
              Pick your stack
            </Typography>

            <Box sx={{ display: 'flex', flexDirection: 'column', gap: 3 }}>
              <TextField
                label="Project name"
                required
                fullWidth
                autoFocus
                value={name}
                onChange={(e) => setName(e.target.value)}
                disabled={createMutation.isPending}
                inputProps={{ autoComplete: 'off', spellCheck: 'false' }}
              />

              <StackPickerFields
                value={stack}
                onChange={setStack}
                extrasRaw={extrasRaw}
                onExtrasRawChange={setExtrasRaw}
                disabled={createMutation.isPending}
              />

              {serverError && <Alert severity="error">{serverError}</Alert>}

              <Box sx={{ display: 'flex', justifyContent: 'flex-end', mt: 1 }}>
                <Button
                  type="submit"
                  variant="contained"
                  size="large"
                  disabled={!canSubmit}
                  startIcon={
                    createMutation.isPending ? (
                      <CircularProgress size={16} color="inherit" />
                    ) : null
                  }
                >
                  {createMutation.isPending ? 'Spawning...' : 'Spawn runtime'}
                </Button>
              </Box>
            </Box>
          </Box>
    </Container>
  )
}

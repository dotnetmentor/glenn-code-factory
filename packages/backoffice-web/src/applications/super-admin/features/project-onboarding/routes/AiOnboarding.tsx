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

/**
 * AI-curated onboarding flow: user describes the project in plain text, we
 * boot an empty runtime, and the agent is meant to immediately propose a
 * stack via the existing curation flow (Spec 16, Scene 2).
 *
 * <p><b>Prompt-kicking is deferred.</b> Sending the user's prompt as the
 * first message to the agent requires the agent-session start endpoint and
 * the project-workspace surface that hosts it (Card 9). This page creates
 * the empty runtime and navigates to the project workspace; the prompt
 * is handed forward via location state so Card 9 can pick it up. If
 * Card 9 ships before this prompt-handoff is wired, the user will land on
 * the workspace and can paste the prompt manually — degraded but not
 * broken.</p>
 */
export function AiOnboarding() {
  const navigate = useNavigate()
  const [prompt, setPrompt] = useState('')
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
        // Card 8 added the runtime workspace surface — that's the canonical
        // landing page after onboarding. The prompt is forwarded via location
        // state for the agent kick-off; actually sending the prompt as the
        // first message requires the agent-session-with-prompt machinery
        // (deferred). The workspace renders the prompt as a banner so the
        // flow doesn't feel broken.
        navigate(
          `/super-admin/projects/${data.projectId}/runtime`,
          { state: { initialPrompt: prompt } },
        )
      },
    },
  })

  const trimmedPrompt = prompt.trim()
  const canSubmit = trimmedPrompt.length > 0 && !createMutation.isPending

  const handleSubmit = (e: React.FormEvent) => {
    e.preventDefault()
    if (!canSubmit) return
    setServerError(null)
    createMutation.mutate({
      data: {
        // No initial spec — the agent will propose one via the existing
        // curation flow once the runtime is online.
        initialSpec: { languages: [], services: [], extras: [] },
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
              Describe what you want to build
            </Typography>

            <Box sx={{ display: 'flex', flexDirection: 'column', gap: 3 }}>
              <TextField
                multiline
                fullWidth
                minRows={6}
                autoFocus
                placeholder="Describe what you want to build..."
                value={prompt}
                onChange={(e) => setPrompt(e.target.value)}
                disabled={createMutation.isPending}
              />

              {serverError && <Alert severity="error">{serverError}</Alert>}

              <Box sx={{ display: 'flex', justifyContent: 'flex-end' }}>
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
                  {createMutation.isPending ? 'Starting...' : 'Start'}
                </Button>
              </Box>
            </Box>
          </Box>
    </Container>
  )
}

import { Alert, Box, Button, Card, CircularProgress, Fade, Stack, TextField, Typography } from '@mui/material'
import { useState, useEffect } from 'react'
import { useNavigate } from 'react-router-dom'
import { usePostApiAuthForgotPassword } from '../../api/queries-commands'

export function ForgotPasswordPage() {
  const navigate = useNavigate()
  const forgotPasswordMutation = usePostApiAuthForgotPassword({})
  const [email, setEmail] = useState('')
  const [submitted, setSubmitted] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [mounted, setMounted] = useState(false)

  useEffect(() => {
    setMounted(true)
  }, [])

  const emailTrimmed = email.trim()
  const isEmailValid = emailTrimmed.includes('@')
  const isSubmitting = forgotPasswordMutation.isPending

  const onSubmit = async () => {
    setError(null)
    if (!isEmailValid) return
    try {
      await forgotPasswordMutation.mutateAsync({ data: { email: emailTrimmed } })
      setSubmitted(true)
    } catch (err: unknown) {
      // Always show success message to avoid leaking whether an email exists
      setSubmitted(true)
      void err
    }
  }

  return (
    <Box
      sx={{
        minHeight: '100vh',
        display: 'flex',
        alignItems: 'center',
        justifyContent: 'center',
        px: 3,
        bgcolor: 'grey.50',
      }}
    >
      <Fade in={mounted} timeout={500}>
        <Box sx={{ width: '100%', maxWidth: 380 }}>
          {/* Header */}
          <Box sx={{ textAlign: 'center', mb: 4 }}>
            <Typography
              sx={{
                fontWeight: 600,
                letterSpacing: '-0.02em',
                color: 'text.primary',
                fontSize: '1.375rem',
                mb: 0.75,
              }}
            >
              {submitted ? 'Check your email' : 'Forgot password'}
            </Typography>
            <Typography sx={{ color: 'text.secondary', fontSize: '0.9375rem' }}>
              {submitted
                ? 'If an account exists with that email, we\'ve sent a password reset link.'
                : 'Enter your email and we\'ll send you a reset link'}
            </Typography>
          </Box>

          {/* Form Card */}
          <Card sx={{ p: 3 }}>
            <Stack spacing={2}>
              {!submitted ? (
                <>
                  <TextField
                    placeholder="Email address"
                    value={email}
                    onChange={(e) => setEmail(e.target.value)}
                    fullWidth
                    autoFocus
                    size="small"
                    onKeyDown={(e) => {
                      if (e.key === 'Enter') {
                        e.preventDefault()
                        if (isEmailValid && !isSubmitting) void onSubmit()
                      }
                    }}
                  />

                  <Button
                    variant="contained"
                    fullWidth
                    onClick={() => { if (isEmailValid && !isSubmitting) void onSubmit() }}
                    disabled={!isEmailValid || isSubmitting}
                    size="large"
                    sx={{
                      py: 1.5,
                      fontSize: '0.9375rem',
                    }}
                  >
                    {isSubmitting && <CircularProgress size={16} sx={{ color: 'inherit', mr: 1 }} />}
                    Send reset link
                  </Button>

                  {error && (
                    <Fade in>
                      <Alert severity="error" sx={{ textAlign: 'center', justifyContent: 'center' }}>
                        {error}
                      </Alert>
                    </Fade>
                  )}
                </>
              ) : (
                <Alert severity="success" sx={{ textAlign: 'center', justifyContent: 'center' }}>
                  If an account exists with that email, we've sent a password reset link.
                </Alert>
              )}

              <Box sx={{ textAlign: 'center', pt: 0.5 }}>
                <Typography
                  sx={{ color: 'primary.main', fontSize: '0.875rem', cursor: 'pointer', '&:hover': { textDecoration: 'underline' } }}
                  onClick={() => navigate('/')}
                >
                  Back to sign in
                </Typography>
              </Box>
            </Stack>
          </Card>
        </Box>
      </Fade>
    </Box>
  )
}

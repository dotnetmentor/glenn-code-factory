import { Alert, Box, Button, Card, CircularProgress, Divider, Fade, Stack, TextField, Typography } from '@mui/material'
import { useState, useEffect } from 'react'
import { useNavigate } from 'react-router-dom'
import { useAuth } from '../authContext'
import { GitHubLoginButton } from './GitHubLoginButton'

export function RegisterPage() {
  const { register, error, clearError, isRegistering, isAuthenticated } = useAuth()
  const navigate = useNavigate()
  const [email, setEmail] = useState('')
  const [password, setPassword] = useState('')
  const [confirmPassword, setConfirmPassword] = useState('')
  const [localError, setLocalError] = useState<string | null>(null)
  const [mounted, setMounted] = useState(false)

  useEffect(() => {
    setMounted(true)
    clearError()
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  useEffect(() => {
    if (isAuthenticated) {
      navigate('/', { replace: true })
    }
  }, [isAuthenticated, navigate])

  const emailTrimmed = email.trim()
  const isEmailValid = emailTrimmed.includes('@')

  const validate = (): boolean => {
    setLocalError(null)
    if (!isEmailValid) {
      setLocalError('Please enter a valid email address.')
      return false
    }
    if (password.length < 6) {
      setLocalError('Password must be at least 6 characters.')
      return false
    }
    if (password !== confirmPassword) {
      setLocalError('Passwords do not match.')
      return false
    }
    return true
  }

  const onSubmit = async () => {
    clearError()
    if (!validate()) return
    await register(emailTrimmed, password)
  }

  const displayError = localError || error

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
              Create an account
            </Typography>
            <Typography sx={{ color: 'text.secondary', fontSize: '0.9375rem' }}>
              Enter your details to get started
            </Typography>
          </Box>

          {/* Form Card */}
          <Card sx={{ p: 3 }}>
            <Stack spacing={2}>
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
                    const pwField = document.getElementById('register-password-field')
                    if (pwField) pwField.focus()
                  }
                }}
              />

              <TextField
                id="register-password-field"
                placeholder="Password"
                type="password"
                value={password}
                onChange={(e) => setPassword(e.target.value)}
                fullWidth
                size="small"
                onKeyDown={(e) => {
                  if (e.key === 'Enter') {
                    e.preventDefault()
                    const confirmField = document.getElementById('register-confirm-password-field')
                    if (confirmField) confirmField.focus()
                  }
                }}
              />

              <TextField
                id="register-confirm-password-field"
                placeholder="Confirm password"
                type="password"
                value={confirmPassword}
                onChange={(e) => setConfirmPassword(e.target.value)}
                fullWidth
                size="small"
                onKeyDown={(e) => {
                  if (e.key === 'Enter') {
                    e.preventDefault()
                    if (!isRegistering) void onSubmit()
                  }
                }}
              />

              <Button
                variant="contained"
                fullWidth
                onClick={() => { if (!isRegistering) void onSubmit() }}
                disabled={isRegistering}
                size="large"
                sx={{
                  py: 1.5,
                  fontSize: '0.9375rem',
                }}
              >
                {isRegistering && <CircularProgress size={16} sx={{ color: 'inherit', mr: 1 }} />}
                Create account
              </Button>

              {displayError && (
                <Fade in>
                  <Alert severity="error" sx={{ textAlign: 'center', justifyContent: 'center' }}>
                    {displayError}
                  </Alert>
                </Fade>
              )}

              <Divider sx={{ my: 0.5, color: 'text.disabled', fontSize: '0.75rem' }}>or</Divider>
              <GitHubLoginButton label="Sign up with GitHub" />

              <Box sx={{ textAlign: 'center', pt: 0.5 }}>
                <Typography sx={{ color: 'text.secondary', fontSize: '0.875rem' }}>
                  {'Already have an account? '}
                  <Typography
                    component="span"
                    sx={{ color: 'primary.main', fontSize: '0.875rem', cursor: 'pointer', '&:hover': { textDecoration: 'underline' } }}
                    onClick={() => navigate('/')}
                  >
                    Sign in
                  </Typography>
                </Typography>
              </Box>
            </Stack>
          </Card>

          <Typography sx={{ color: 'text.disabled', fontSize: '0.8125rem', textAlign: 'center', mt: 3 }}>
            Secure account registration
          </Typography>
        </Box>
      </Fade>
    </Box>
  )
}

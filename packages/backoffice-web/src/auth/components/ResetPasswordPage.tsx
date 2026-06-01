import { Alert, Box, Button, Card, CircularProgress, Fade, Stack, TextField, Typography } from '@mui/material'
import { useState, useEffect } from 'react'
import { useNavigate, useSearchParams } from 'react-router-dom'
import { usePostApiAuthResetPassword } from '../../api/queries-commands'

export function ResetPasswordPage() {
  const navigate = useNavigate()
  const [searchParams] = useSearchParams()
  const resetPasswordMutation = usePostApiAuthResetPassword({})

  const emailFromUrl = searchParams.get('email') ?? ''
  const tokenFromUrl = searchParams.get('token') ?? ''

  const [newPassword, setNewPassword] = useState('')
  const [confirmPassword, setConfirmPassword] = useState('')
  const [localError, setLocalError] = useState<string | null>(null)
  const [success, setSuccess] = useState(false)
  const [mounted, setMounted] = useState(false)

  useEffect(() => {
    setMounted(true)
  }, [])

  const isSubmitting = resetPasswordMutation.isPending

  const validate = (): boolean => {
    setLocalError(null)
    if (!emailFromUrl || !tokenFromUrl) {
      setLocalError('Invalid or expired reset link. Please request a new one.')
      return false
    }
    if (newPassword.length < 6) {
      setLocalError('Password must be at least 6 characters.')
      return false
    }
    if (newPassword !== confirmPassword) {
      setLocalError('Passwords do not match.')
      return false
    }
    return true
  }

  const onSubmit = async () => {
    setLocalError(null)
    if (!validate()) return
    try {
      await resetPasswordMutation.mutateAsync({
        data: {
          email: emailFromUrl,
          token: tokenFromUrl,
          newPassword,
          confirmPassword,
        },
      })
      setSuccess(true)
    } catch (err: unknown) {
      const axiosErr = err as { response?: { data?: { detail?: string } } }
      const msg = axiosErr?.response?.data?.detail || (err instanceof Error ? err.message : 'Failed to reset password. The link may have expired.')
      setLocalError(msg)
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
              {success ? 'Password reset' : 'Set new password'}
            </Typography>
            <Typography sx={{ color: 'text.secondary', fontSize: '0.9375rem' }}>
              {success
                ? 'Your password has been updated. You can now sign in.'
                : 'Enter your new password below'}
            </Typography>
          </Box>

          {/* Form Card */}
          <Card sx={{ p: 3 }}>
            <Stack spacing={2}>
              {!success ? (
                <>
                  <TextField
                    placeholder="New password"
                    type="password"
                    value={newPassword}
                    onChange={(e) => setNewPassword(e.target.value)}
                    fullWidth
                    autoFocus
                    size="small"
                    onKeyDown={(e) => {
                      if (e.key === 'Enter') {
                        e.preventDefault()
                        const confirmField = document.getElementById('reset-confirm-password-field')
                        if (confirmField) confirmField.focus()
                      }
                    }}
                  />

                  <TextField
                    id="reset-confirm-password-field"
                    placeholder="Confirm new password"
                    type="password"
                    value={confirmPassword}
                    onChange={(e) => setConfirmPassword(e.target.value)}
                    fullWidth
                    size="small"
                    onKeyDown={(e) => {
                      if (e.key === 'Enter') {
                        e.preventDefault()
                        if (!isSubmitting) void onSubmit()
                      }
                    }}
                  />

                  <Button
                    variant="contained"
                    fullWidth
                    onClick={() => { if (!isSubmitting) void onSubmit() }}
                    disabled={isSubmitting}
                    size="large"
                    sx={{
                      py: 1.5,
                      fontSize: '0.9375rem',
                    }}
                  >
                    {isSubmitting && <CircularProgress size={16} sx={{ color: 'inherit', mr: 1 }} />}
                    Reset password
                  </Button>

                  {localError && (
                    <Fade in>
                      <Alert severity="error" sx={{ textAlign: 'center', justifyContent: 'center' }}>
                        {localError}
                      </Alert>
                    </Fade>
                  )}
                </>
              ) : (
                <Alert severity="success" sx={{ textAlign: 'center', justifyContent: 'center' }}>
                  Password reset successfully. You can now sign in.
                </Alert>
              )}

              <Box sx={{ textAlign: 'center', pt: 0.5 }}>
                <Typography
                  sx={{ color: 'primary.main', fontSize: '0.875rem', cursor: 'pointer', '&:hover': { textDecoration: 'underline' } }}
                  onClick={() => navigate('/')}
                >
                  {success ? 'Sign in' : 'Back to sign in'}
                </Typography>
              </Box>
            </Stack>
          </Card>
        </Box>
      </Fade>
    </Box>
  )
}

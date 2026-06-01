import { Alert, Box, Button, Card, CircularProgress, Divider, Fade, Stack, TextField, Typography } from '@mui/material'
import { useState, useEffect } from 'react'
import { useSearchParams, useNavigate } from 'react-router-dom'
import { useAuth } from '../authContext'
import { GitHubLoginButton } from './GitHubLoginButton'

const isDev = import.meta.env.DEV
const DEV_TEST_EMAIL = 'test@test.com'
const DEV_TEST_OTP = '123456'
const DEV_TEST_PASSWORD = 'Test123!'

export interface LoginFormProps {
  variant?: 'page' | 'compact'
  onSuccess?: () => void
}

type LoginMode = 'otp' | 'password'

export function LoginForm({ variant = 'page', onSuccess }: LoginFormProps) {
  const {
    sendOtp, verifyOtp, loginWithPassword,
    otpStep, otpEmail, error, clearError, backToEmail,
    isSendingOtp, isVerifyingOtp, isLoggingIn, isAuthenticated,
  } = useAuth()
  const [searchParams] = useSearchParams()
  const navigate = useNavigate()
  const [email, setEmail] = useState(isDev ? DEV_TEST_EMAIL : '')
  const [code, setCode] = useState(isDev ? DEV_TEST_OTP : '')
  const [password, setPassword] = useState(isDev ? DEV_TEST_PASSWORD : '')
  const [loginMode, setLoginMode] = useState<LoginMode>('otp')
  const [mounted, setMounted] = useState(false)

  useEffect(() => {
    setMounted(true)
  }, [])

  useEffect(() => {
    const emailFromUrl = searchParams.get('email')
    if (emailFromUrl && emailFromUrl.includes('@')) {
      setEmail(emailFromUrl)
    }
  }, [searchParams])

  useEffect(() => {
    if (isAuthenticated && onSuccess) {
      onSuccess()
    }
  }, [isAuthenticated, onSuccess])

  const emailTrimmed = email.trim()
  const isEmailValid = emailTrimmed.includes('@')
  const codeTrimmed = code.trim()

  const onSend = async () => {
    clearError()
    if (!isEmailValid) return
    await sendOtp(emailTrimmed)
  }

  const onVerify = async () => {
    await verifyOtp(otpEmail ?? emailTrimmed, codeTrimmed)
  }

  const onPasswordLogin = async () => {
    clearError()
    if (!isEmailValid || !password) return
    await loginWithPassword(emailTrimmed, password)
  }

  const switchMode = (mode: LoginMode) => {
    clearError()
    setLoginMode(mode)
  }

  const isOtpSent = otpStep === 'email-sent'
  const isCompact = variant === 'compact'

  const linkButtonSx = {
    color: 'text.secondary',
    fontSize: '0.875rem',
    p: 0,
    minWidth: 'auto',
    textTransform: 'none' as const,
    '&:hover': { color: 'grey.700', bgcolor: 'transparent' },
  }

  // Determine header text
  let headerTitle = 'Welcome back'
  let headerSubtitle = 'Sign in to continue'
  if (isOtpSent && loginMode === 'otp') {
    headerTitle = 'Check your email'
    headerSubtitle = `We sent a code to ${otpEmail}`
  } else if (loginMode === 'password') {
    headerTitle = 'Welcome back'
    headerSubtitle = 'Sign in with your password'
  }

  const formContent = (
    <Box sx={{ width: '100%', maxWidth: isCompact ? '100%' : 380 }}>
      {/* Header */}
      <Box sx={{ textAlign: 'center', mb: isCompact ? 3 : 4 }}>
        <Typography
          sx={{
            fontWeight: 600,
            letterSpacing: '-0.02em',
            color: 'text.primary',
            fontSize: isCompact ? '1.25rem' : '1.375rem',
            mb: 0.75,
          }}
        >
          {headerTitle}
        </Typography>
        <Typography sx={{ color: 'text.secondary', fontSize: '0.9375rem' }}>
          {headerSubtitle}
        </Typography>
      </Box>

      {/* Form Card */}
      <Card
        sx={{
          p: isCompact ? 0 : 3,
          ...(isCompact && { border: 'none', boxShadow: 'none', bgcolor: 'transparent' }),
        }}
      >
        <Stack spacing={2}>
          {/* OTP Mode - Email Input */}
          {loginMode === 'otp' && !isOtpSent && (
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
                    if (isEmailValid) void onSend()
                  }
                }}
              />

              <Button
                variant="contained"
                fullWidth
                onClick={() => { if (isEmailValid && !isSendingOtp) void onSend() }}
                disabled={!isEmailValid || isSendingOtp}
                size="large"
                sx={{
                  py: 1.5,
                  fontSize: '0.9375rem',
                }}
              >
                {isSendingOtp && <CircularProgress size={16} sx={{ color: 'inherit', mr: 1 }} />}
                Continue
              </Button>

              <Box sx={{ textAlign: 'center', pt: 0.5 }}>
                <Button
                  variant="text"
                  size="small"
                  onClick={() => switchMode('password')}
                  sx={linkButtonSx}
                >
                  Use password instead
                </Button>
              </Box>

              <Box sx={{ textAlign: 'center' }}>
                <Typography sx={{ color: 'text.secondary', fontSize: '0.875rem' }}>
                  {"Don't have an account? "}
                  <Typography
                    component="span"
                    sx={{ color: 'primary.main', fontSize: '0.875rem', cursor: 'pointer', '&:hover': { textDecoration: 'underline' } }}
                    onClick={() => navigate('/register')}
                  >
                    Create an account
                  </Typography>
                </Typography>
              </Box>

              {isDev && (
                <Typography sx={{ color: 'text.disabled', fontSize: '0.8125rem', textAlign: 'center', pt: 0.5 }}>
                  Dev: {DEV_TEST_EMAIL} / OTP: {DEV_TEST_OTP} / Pass: {DEV_TEST_PASSWORD}
                </Typography>
              )}
            </>
          )}

          {/* Password Mode */}
          {loginMode === 'password' && !isOtpSent && (
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
                    // Focus the password field
                    const pwField = document.getElementById('login-password-field')
                    if (pwField) pwField.focus()
                  }
                }}
              />

              <TextField
                id="login-password-field"
                placeholder="Password"
                type="password"
                value={password}
                onChange={(e) => setPassword(e.target.value)}
                fullWidth
                size="small"
                onKeyDown={(e) => {
                  if (e.key === 'Enter') {
                    e.preventDefault()
                    if (isEmailValid && password) void onPasswordLogin()
                  }
                }}
              />

              <Box sx={{ textAlign: 'right', mt: -0.5 }}>
                <Button
                  variant="text"
                  size="small"
                  onClick={() => navigate('/forgot-password')}
                  sx={{ ...linkButtonSx, fontSize: '0.8125rem' }}
                >
                  Forgot password?
                </Button>
              </Box>

              <Button
                variant="contained"
                fullWidth
                onClick={() => { if (isEmailValid && password && !isLoggingIn) void onPasswordLogin() }}
                disabled={!isEmailValid || !password || isLoggingIn}
                size="large"
                sx={{
                  py: 1.5,
                  fontSize: '0.9375rem',
                }}
              >
                {isLoggingIn && <CircularProgress size={16} sx={{ color: 'inherit', mr: 1 }} />}
                Sign in
              </Button>

              <Box sx={{ textAlign: 'center', pt: 0.5 }}>
                <Button
                  variant="text"
                  size="small"
                  onClick={() => switchMode('otp')}
                  sx={linkButtonSx}
                >
                  Use magic code instead
                </Button>
              </Box>

              <Box sx={{ textAlign: 'center' }}>
                <Typography sx={{ color: 'text.secondary', fontSize: '0.875rem' }}>
                  {"Don't have an account? "}
                  <Typography
                    component="span"
                    sx={{ color: 'primary.main', fontSize: '0.875rem', cursor: 'pointer', '&:hover': { textDecoration: 'underline' } }}
                    onClick={() => navigate('/register')}
                  >
                    Create an account
                  </Typography>
                </Typography>
              </Box>

              {isDev && (
                <Typography sx={{ color: 'text.disabled', fontSize: '0.8125rem', textAlign: 'center', pt: 0.5 }}>
                  Dev: {DEV_TEST_EMAIL} / OTP: {DEV_TEST_OTP} / Pass: {DEV_TEST_PASSWORD}
                </Typography>
              )}
            </>
          )}

          {/* OTP Sent State (unchanged) */}
          {isOtpSent && (
            <>
              <TextField
                placeholder="000000"
                value={code}
                onChange={(e) => setCode(e.target.value)}
                fullWidth
                autoFocus
                size="small"
                inputProps={{
                  style: {
                    textAlign: 'center',
                    fontSize: '1.375rem',
                    letterSpacing: '0.2em',
                    fontWeight: 500,
                    fontFamily: 'ui-monospace, SF Mono, Monaco, Consolas, monospace',
                  },
                  maxLength: 6,
                }}
                onKeyDown={(e) => {
                  if (e.key === 'Enter') {
                    e.preventDefault()
                    if (codeTrimmed) void onVerify()
                  }
                }}
              />

              <Button
                variant="contained"
                fullWidth
                onClick={() => { if (codeTrimmed && !isVerifyingOtp) void onVerify() }}
                disabled={!codeTrimmed || isVerifyingOtp}
                size="large"
                sx={{
                  py: 1.5,
                  fontSize: '0.9375rem',
                }}
              >
                {isVerifyingOtp && <CircularProgress size={16} sx={{ color: 'inherit', mr: 1 }} />}
                Verify
              </Button>

              <Stack direction="row" justifyContent="space-between" alignItems="center" sx={{ pt: 0.5 }}>
                <Button
                  variant="text"
                  size="small"
                  onClick={backToEmail}
                  sx={linkButtonSx}
                >
                  Back
                </Button>
                <Button
                  variant="text"
                  size="small"
                  onClick={() => { if (!isSendingOtp) void onSend() }}
                  disabled={isSendingOtp}
                  sx={linkButtonSx}
                >
                  {isSendingOtp ? 'Sending...' : 'Resend'}
                </Button>
              </Stack>
            </>
          )}

          {error && (
            <Fade in>
              <Alert severity="error" sx={{ textAlign: 'center', justifyContent: 'center' }}>
                {error}
              </Alert>
            </Fade>
          )}

          {!isOtpSent && (
            <>
              <Divider sx={{ my: 0.5, color: 'text.disabled', fontSize: '0.75rem' }}>or</Divider>
              <GitHubLoginButton />
            </>
          )}
        </Stack>
      </Card>

      {!isCompact && (
        <Typography sx={{ color: 'text.disabled', fontSize: '0.8125rem', textAlign: 'center', mt: 3 }}>
          {loginMode === 'otp' ? 'Secure passwordless sign in' : 'Secure password sign in'}
        </Typography>
      )}
    </Box>
  )

  if (isCompact) {
    return formContent
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
        {formContent}
      </Fade>
    </Box>
  )
}

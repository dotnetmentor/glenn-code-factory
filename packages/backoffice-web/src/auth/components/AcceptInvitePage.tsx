import { useEffect, useRef, useState } from 'react'
import { useNavigate, useParams } from 'react-router-dom'
import {
  Alert,
  Box,
  Button,
  Card,
  CircularProgress,
  Stack,
  Typography,
} from '@mui/material'
import { useAuth } from '../authContext'
import {
  usePostApiInvitesAccept,
  type ProblemDetails,
} from '../../api/queries-commands'

const INVITE_TOKEN_STORAGE_KEY = 'pending-invite-token'

function readErrorDetail(err: unknown): string | null {
  const maybe = err as { response?: { data?: ProblemDetails } } | undefined
  return maybe?.response?.data?.detail ?? maybe?.response?.data?.title ?? null
}

export function AcceptInvitePage() {
  const params = useParams<{ token: string }>()
  const navigate = useNavigate()
  const { isAuthenticated, isLoading: isAuthLoading, refetchAuth } = useAuth()

  const token = params.token ?? ''
  const acceptInvite = usePostApiInvitesAccept()

  const [error, setError] = useState<string | null>(null)
  const acceptedRef = useRef(false)

  useEffect(() => {
    if (isAuthLoading) return
    if (!token) {
      setError('This invite link is missing a token.')
      return
    }
    if (!isAuthenticated) return
    if (acceptedRef.current) return
    acceptedRef.current = true

    acceptInvite.mutate(
      { data: { token } },
      {
        onSuccess: async (response) => {
          // Clear any persisted token now that it's been redeemed.
          try {
            sessionStorage.removeItem(INVITE_TOKEN_STORAGE_KEY)
          } catch {
            /* ignore storage errors */
          }
          // Refresh the /me/workspaces query so the new workspace appears in the picker.
          await refetchAuth()
          navigate(`/w/${response.slug}`, { replace: true })
        },
        onError: (err) => {
          setError(
            readErrorDetail(err)
              ?? 'We could not accept this invite. It may have expired or been revoked.',
          )
        },
      },
    )
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [isAuthLoading, isAuthenticated, token])

  const persistTokenAndGo = (target: string) => {
    try {
      if (token) sessionStorage.setItem(INVITE_TOKEN_STORAGE_KEY, token)
    } catch {
      /* ignore storage errors */
    }
    navigate(target)
  }

  if (isAuthLoading) {
    return (
      <CenteredCard>
        <Box sx={{ display: 'flex', justifyContent: 'center', py: 2 }}>
          <CircularProgress size={24} />
        </Box>
      </CenteredCard>
    )
  }

  // Not signed in: prompt the user to sign in or register, persist token first.
  if (!isAuthenticated) {
    return (
      <CenteredCard>
        <Stack spacing={2}>
          <Typography variant="h5" component="h1" sx={{ fontWeight: 600 }}>
            You've been invited to join a workspace
          </Typography>
          <Typography variant="body2" color="text.secondary">
            Sign in or create an account with the email address that received this
            invite to join the workspace.
          </Typography>
          {/*
            TODO: after sign-in/registration, auto-resume the accept flow using
            the token persisted in sessionStorage[INVITE_TOKEN_STORAGE_KEY].
          */}
          <Stack direction="row" spacing={1.5} sx={{ pt: 1 }}>
            <Button
              variant="contained"
              fullWidth
              onClick={() => persistTokenAndGo('/')}
            >
              Sign in
            </Button>
            <Button
              variant="outlined"
              fullWidth
              onClick={() =>
                persistTokenAndGo(
                  `/register?inviteToken=${encodeURIComponent(token)}`,
                )
              }
            >
              Register
            </Button>
          </Stack>
        </Stack>
      </CenteredCard>
    )
  }

  // Signed in: showing progress / error while the mutation runs.
  return (
    <CenteredCard>
      <Stack spacing={2} alignItems="center">
        <Typography variant="h5" component="h1" sx={{ fontWeight: 600 }}>
          Accepting your invite
        </Typography>

        {!error && (
          <Box sx={{ display: 'flex', justifyContent: 'center', py: 2 }}>
            <CircularProgress size={24} />
          </Box>
        )}

        {error && (
          <>
            <Alert severity="error" sx={{ width: '100%' }}>
              {error}
            </Alert>
            <Button variant="text" onClick={() => navigate('/')}>
              Go to dashboard
            </Button>
          </>
        )}
      </Stack>
    </CenteredCard>
  )
}

function CenteredCard({ children }: { children: React.ReactNode }) {
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
      <Box sx={{ width: '100%', maxWidth: 420 }}>
        <Card sx={{ p: 4 }}>{children}</Card>
      </Box>
    </Box>
  )
}

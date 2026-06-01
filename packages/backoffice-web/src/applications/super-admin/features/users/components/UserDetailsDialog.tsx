import { useEffect } from 'react'
import {
  Alert,
  Avatar,
  Dialog,
  DialogContent,
  Box,
  Typography,
  Stack,
  TextField,
  CircularProgress,
  Switch,
  IconButton,
  Fade,
} from '@mui/material'
import { useTheme } from '@mui/material/styles'
import { useForm } from 'react-hook-form'
import { zodResolver } from '@hookform/resolvers/zod'
import { z } from 'zod'
import CloseIcon from '@mui/icons-material/Close'
import { type UserListItem } from '../../../../../api/queries-commands'
import { putApiUsersIdBody } from '../../../../../api/zod'
import { useUserManagement } from '../hooks/useUserManagement'
import { AVAILABLE_ROLES } from '../../../../shared/constants/roles'
import { useState } from 'react'

type UpdateUserFormData = z.infer<typeof putApiUsersIdBody>

interface UserDetailsDialogProps {
  open: boolean
  user: UserListItem | null
  onClose: () => void
  onNotify: (message: string, severity: 'success' | 'error') => void
}

// Minimal button component for this dialog
function ActionButton({
  children,
  onClick,
  variant = 'secondary',
  disabled = false,
  loading = false,
  type = 'button',
}: {
  children: React.ReactNode
  onClick?: () => void
  variant?: 'primary' | 'secondary' | 'text'
  disabled?: boolean
  loading?: boolean
  type?: 'button' | 'submit'
}) {
  const theme = useTheme()

  const baseStyles = {
    px: 2.5,
    py: 1,
    borderRadius: 1.5,
    fontSize: '0.875rem',
    fontWeight: 500,
    cursor: disabled ? 'default' : 'pointer',
    transition: 'all 0.15s ease',
    border: 'none',
    display: 'inline-flex',
    alignItems: 'center',
    gap: 1,
    opacity: disabled ? 0.5 : 1,
  }

  const variants = {
    primary: {
      ...baseStyles,
      backgroundColor: theme.palette.primary.main,
      color: theme.palette.primary.contrastText,
      '&:hover': !disabled ? { backgroundColor: theme.palette.primary.dark } : {},
    },
    secondary: {
      ...baseStyles,
      backgroundColor: theme.palette.grey[100],
      color: theme.palette.text.primary,
      '&:hover': !disabled ? { backgroundColor: theme.palette.grey[200] } : {},
    },
    text: {
      ...baseStyles,
      backgroundColor: 'transparent',
      color: theme.palette.text.secondary,
      '&:hover': !disabled ? { color: theme.palette.text.primary } : {},
    },
  }

  return (
    <Box
      component={type === 'submit' ? 'button' : 'button'}
      type={type}
      onClick={disabled ? undefined : onClick}
      sx={variants[variant]}
    >
      {loading && <CircularProgress size={14} sx={{ color: 'inherit' }} />}
      {children}
    </Box>
  )
}

// Field display for view mode
function Field({ label, value }: { label: string; value: string | null | undefined }) {
  const theme = useTheme()

  return (
    <Box>
      <Typography
        sx={{
          fontSize: '0.6875rem',
          fontWeight: 500,
          textTransform: 'uppercase',
          letterSpacing: '0.05em',
          color: theme.palette.text.disabled,
          mb: 0.5,
        }}
      >
        {label}
      </Typography>
      <Typography
        sx={{
          fontSize: '0.9375rem',
          color: value ? theme.palette.text.primary : theme.palette.text.disabled,
        }}
      >
        {value || '\u2014'}
      </Typography>
    </Box>
  )
}

export function UserDetailsDialog({ open, user, onClose, onNotify }: UserDetailsDialogProps) {
  const theme = useTheme()
  const [isEditMode, setIsEditMode] = useState(false)
  const [selectedRoles, setSelectedRoles] = useState<string[]>([])

  const userId = user?.id ?? ''

  const {
    user: userDetails,
    isLoadingUser,
    isUpdatingInfo,
    isUpdatingRoles,
    updateInfo,
    updateRoles,
  } = useUserManagement({
    userId,
    onSuccess: (message) => {
      onNotify(message, 'success')
      setIsEditMode(false)
    },
    onError: (error) => onNotify(error, 'error'),
  })

  const form = useForm<UpdateUserFormData>({
    resolver: zodResolver(putApiUsersIdBody),
    defaultValues: {
      firstName: '',
      lastName: '',
      email: '',
    },
  })

  useEffect(() => {
    if (open && userDetails) {
      form.reset({
        firstName: userDetails.firstName ?? '',
        lastName: userDetails.lastName ?? '',
        email: userDetails.email ?? '',
      })
      setSelectedRoles(user?.roles ?? [])
      setIsEditMode(false)
    }
  }, [open, userDetails, user?.roles, form])

  if (!user) return null

  const userName = userDetails?.fullName ?? user.fullName ?? user.email ?? 'User'
  const userInitial = (userName[0] ?? 'U').toUpperCase()

  const handleSaveInfo = form.handleSubmit((data) => {
    updateInfo({
      firstName: data.firstName ?? undefined,
      lastName: data.lastName ?? undefined,
      email: data.email ?? undefined,
    })
  })

  const handleCancelEdit = () => {
    form.reset({
      firstName: userDetails?.firstName ?? '',
      lastName: userDetails?.lastName ?? '',
      email: userDetails?.email ?? '',
    })
    setIsEditMode(false)
  }

  const handleSaveRoles = () => {
    updateRoles(selectedRoles)
  }

  const handleToggleRole = (role: string) => {
    setSelectedRoles(prev =>
      prev.includes(role) ? prev.filter(r => r !== role) : [...prev, role]
    )
  }

  const rolesChanged = JSON.stringify(selectedRoles.sort()) !== JSON.stringify((user?.roles ?? []).sort())

  return (
    <Dialog
      open={open}
      onClose={onClose}
      maxWidth="sm"
      fullWidth
      PaperProps={{
        sx: {
          borderRadius: 3,
          overflow: 'hidden',
        },
      }}
    >
      {/* Header */}
      <Box
        sx={{
          px: 3,
          py: 2.5,
          display: 'flex',
          alignItems: 'center',
          gap: 2,
          borderBottom: `1px solid ${theme.palette.grey[200]}`,
        }}
      >
        {/* Avatar */}
        <Avatar
          sx={{
            width: 48,
            height: 48,
            borderRadius: 2,
            bgcolor: 'grey.100',
            color: 'text.secondary',
            fontSize: '1.125rem',
            fontWeight: 600,
            flexShrink: 0,
          }}
          variant="rounded"
        >
          {userInitial}
        </Avatar>

        {/* Name & Email */}
        <Box sx={{ flex: 1, minWidth: 0 }}>
          <Typography
            sx={{
              fontSize: '1.0625rem',
              fontWeight: 600,
              color: theme.palette.text.primary,
              lineHeight: 1.3,
            }}
          >
            {userName}
          </Typography>
          <Typography
            sx={{
              fontSize: '0.875rem',
              color: theme.palette.text.secondary,
              mt: 0.25,
            }}
          >
            {userDetails?.email ?? user.email}
          </Typography>
        </Box>

        {/* Close button */}
        <IconButton
          onClick={onClose}
          size="small"
          sx={{
            color: theme.palette.text.disabled,
            '&:hover': {
              color: theme.palette.text.secondary,
              backgroundColor: theme.palette.grey[100],
            },
          }}
        >
          <CloseIcon sx={{ fontSize: 20 }} />
        </IconButton>
      </Box>

      <DialogContent sx={{ p: 0 }}>
        {isLoadingUser ? (
          <Box sx={{ display: 'flex', justifyContent: 'center', py: 6 }}>
            <CircularProgress size={24} sx={{ color: theme.palette.text.disabled }} />
          </Box>
        ) : (
          <>
            {/* User Information Section */}
            <Box sx={{ px: 3, py: 2.5 }}>
              <Box
                sx={{
                  display: 'flex',
                  alignItems: 'center',
                  justifyContent: 'space-between',
                  mb: 2,
                }}
              >
                <Typography
                  sx={{
                    fontSize: '0.6875rem',
                    fontWeight: 500,
                    textTransform: 'uppercase',
                    letterSpacing: '0.05em',
                    color: theme.palette.text.disabled,
                  }}
                >
                  Information
                </Typography>
                {!isEditMode && (
                  <Box
                    component="button"
                    onClick={() => setIsEditMode(true)}
                    sx={{
                      background: 'none',
                      border: 'none',
                      padding: 0,
                      fontSize: '0.8125rem',
                      fontWeight: 500,
                      color: theme.palette.primary.main,
                      cursor: 'pointer',
                      '&:hover': { textDecoration: 'underline' },
                    }}
                  >
                    Edit
                  </Box>
                )}
              </Box>

              {!isEditMode ? (
                <Stack spacing={2}>
                  <Box sx={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: 2 }}>
                    <Field label="First name" value={userDetails?.firstName} />
                    <Field label="Last name" value={userDetails?.lastName} />
                  </Box>
                  <Field label="Email" value={userDetails?.email} />
                  <Field
                    label="Created"
                    value={userDetails?.createdAt ? new Date(userDetails.createdAt).toLocaleDateString('en-US', {
                      year: 'numeric',
                      month: 'long',
                      day: 'numeric',
                    }) : null}
                  />
                </Stack>
              ) : (
                <form onSubmit={handleSaveInfo}>
                  <Stack spacing={2}>
                    <Box sx={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: 2 }}>
                      <TextField
                        label="First name"
                        fullWidth
                        size="small"
                        disabled={isUpdatingInfo}
                        error={!!form.formState.errors.firstName}
                        helperText={form.formState.errors.firstName?.message}
                        {...form.register('firstName')}
                      />
                      <TextField
                        label="Last name"
                        fullWidth
                        size="small"
                        disabled={isUpdatingInfo}
                        error={!!form.formState.errors.lastName}
                        helperText={form.formState.errors.lastName?.message}
                        {...form.register('lastName')}
                      />
                    </Box>
                    <TextField
                      label="Email"
                      type="email"
                      fullWidth
                      size="small"
                      disabled={isUpdatingInfo}
                      error={!!form.formState.errors.email}
                      helperText={form.formState.errors.email?.message}
                      {...form.register('email')}
                    />
                    <Box sx={{ display: 'flex', gap: 1, pt: 1 }}>
                      <ActionButton
                        type="submit"
                        variant="primary"
                        disabled={isUpdatingInfo}
                        loading={isUpdatingInfo}
                      >
                        Save
                      </ActionButton>
                      <ActionButton
                        variant="text"
                        onClick={handleCancelEdit}
                        disabled={isUpdatingInfo}
                      >
                        Cancel
                      </ActionButton>
                    </Box>
                  </Stack>
                </form>
              )}
            </Box>

            {/* Divider */}
            <Box sx={{ height: 1, backgroundColor: theme.palette.grey[200], mx: 3 }} />

            {/* Roles Section */}
            <Box sx={{ px: 3, py: 2.5 }}>
              <Typography
                sx={{
                  fontSize: '0.6875rem',
                  fontWeight: 500,
                  textTransform: 'uppercase',
                  letterSpacing: '0.05em',
                  color: theme.palette.text.disabled,
                  mb: 2,
                }}
              >
                Roles
              </Typography>

              <Stack spacing={0}>
                {AVAILABLE_ROLES.map((role: string) => (
                  <Box
                    key={role}
                    sx={{
                      display: 'flex',
                      alignItems: 'center',
                      justifyContent: 'space-between',
                      py: 1.25,
                      borderBottom: `1px solid ${theme.palette.grey[100]}`,
                      '&:last-child': { borderBottom: 'none' },
                    }}
                  >
                    <Typography
                      sx={{
                        fontSize: '0.9375rem',
                        color: theme.palette.text.primary,
                      }}
                    >
                      {role}
                    </Typography>
                    <Switch
                      checked={selectedRoles.includes(role)}
                      onChange={() => handleToggleRole(role)}
                      disabled={isUpdatingRoles}
                      size="small"
                    />
                  </Box>
                ))}
              </Stack>

              {selectedRoles.length === 0 && (
                <Alert severity="warning" sx={{ mt: 1.5 }}>
                  No roles assigned
                </Alert>
              )}

              {rolesChanged && (
                <Fade in>
                  <Box sx={{ mt: 2 }}>
                    <ActionButton
                      variant="primary"
                      onClick={handleSaveRoles}
                      disabled={isUpdatingRoles}
                      loading={isUpdatingRoles}
                    >
                      Save roles
                    </ActionButton>
                  </Box>
                </Fade>
              )}
            </Box>
          </>
        )}
      </DialogContent>
    </Dialog>
  )
}

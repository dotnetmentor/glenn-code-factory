import { useEffect, useMemo, useState } from 'react'
import {
  Alert,
  Button,
  Dialog,
  DialogActions,
  DialogContent,
  DialogTitle,
  FormControl,
  InputLabel,
  MenuItem,
  Select,
  Stack,
  TextField,
} from '@mui/material'
import { useQueryClient } from '@tanstack/react-query'
import {
  getGetApiWorkspacesSlugInvitesQueryKey,
  usePostApiWorkspacesSlugInvites,
  WorkspaceRole,
} from '../../../../../api/queries-commands'
import type { ProblemDetails } from '../../../../../api/queries-commands'
import { useNotification } from '../../../../shared/contexts/NotificationContext'

const EMAIL_REGEX = /^[^\s@]+@[^\s@]+\.[^\s@]+$/

interface InviteDialogProps {
  open: boolean
  onClose: () => void
  slug: string
  /**
   * Whether the current viewer can issue an Owner invite. When false (viewer
   * is Admin), the Owner option is omitted from the role select.
   */
  canInviteOwner: boolean
}

export function InviteDialog({ open, onClose, slug, canInviteOwner }: InviteDialogProps) {
  const queryClient = useQueryClient()
  const { showSuccess } = useNotification()

  const [email, setEmail] = useState('')
  const [role, setRole] = useState<WorkspaceRole>(WorkspaceRole.Member)
  const [touched, setTouched] = useState({ email: false, role: false })
  const [serverError, setServerError] = useState<string | null>(null)

  const createInvite = usePostApiWorkspacesSlugInvites()

  // Reset form whenever the dialog opens.
  useEffect(() => {
    if (open) {
      setEmail('')
      setRole(WorkspaceRole.Member)
      setTouched({ email: false, role: false })
      setServerError(null)
    }
  }, [open])

  const trimmedEmail = email.trim()

  const emailError = useMemo(() => {
    if (!trimmedEmail) return 'Email is required.'
    if (!EMAIL_REGEX.test(trimmedEmail)) return 'Enter a valid email address.'
    return null
  }, [trimmedEmail])

  const roleError = role ? null : 'Role is required.'

  const isValid = !emailError && !roleError

  const handleSubmit = (e?: React.FormEvent) => {
    e?.preventDefault()
    setTouched({ email: true, role: true })
    setServerError(null)
    if (!isValid) return

    createInvite.mutate(
      { slug, data: { email: trimmedEmail, role } },
      {
        onSuccess: () => {
          showSuccess(`Invite sent to ${trimmedEmail}.`)
          queryClient.invalidateQueries({ queryKey: getGetApiWorkspacesSlugInvitesQueryKey(slug) })
          onClose()
        },
        onError: (error) => {
          const detail = (error?.response?.data as ProblemDetails | undefined)?.detail
            ?? (error?.response?.data as ProblemDetails | undefined)?.title
            ?? 'Could not send the invite. Please try again.'
          setServerError(detail)
        },
      },
    )
  }

  return (
    <Dialog open={open} onClose={createInvite.isPending ? undefined : onClose} fullWidth maxWidth="xs">
      <form onSubmit={handleSubmit} noValidate>
        <DialogTitle>Invite member</DialogTitle>
        <DialogContent>
          <Stack spacing={2.5} sx={{ pt: 1 }}>
            <TextField
              label="Email"
              type="email"
              value={email}
              onChange={(e) => setEmail(e.target.value)}
              onBlur={() => setTouched((t) => ({ ...t, email: true }))}
              error={touched.email && !!emailError}
              helperText={touched.email && emailError ? emailError : ' '}
              autoFocus
              fullWidth
              size="small"
              inputProps={{ 'aria-label': 'Email' }}
              disabled={createInvite.isPending}
            />
            <FormControl
              fullWidth
              size="small"
              error={touched.role && !!roleError}
              disabled={createInvite.isPending}
            >
              <InputLabel id="invite-role-label">Role</InputLabel>
              <Select
                labelId="invite-role-label"
                label="Role"
                value={role}
                onChange={(e) => setRole(e.target.value as WorkspaceRole)}
                onBlur={() => setTouched((t) => ({ ...t, role: true }))}
                inputProps={{ 'aria-label': 'Role' }}
              >
                {canInviteOwner && <MenuItem value={WorkspaceRole.Owner}>Owner</MenuItem>}
                <MenuItem value={WorkspaceRole.Admin}>Admin</MenuItem>
                <MenuItem value={WorkspaceRole.Member}>Member</MenuItem>
              </Select>
            </FormControl>

            {serverError && <Alert severity="error">{serverError}</Alert>}
          </Stack>
        </DialogContent>
        <DialogActions sx={{ px: 3, pb: 2 }}>
          <Button onClick={onClose} disabled={createInvite.isPending} color="inherit">
            Cancel
          </Button>
          <Button
            type="submit"
            variant="contained"
            disabled={!isValid || createInvite.isPending}
          >
            {createInvite.isPending ? 'Sending…' : 'Send invite'}
          </Button>
        </DialogActions>
      </form>
    </Dialog>
  )
}

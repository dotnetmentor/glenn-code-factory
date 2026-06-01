import { useMemo, useState } from 'react'
import {
  Alert,
  Avatar,
  Box,
  Button,
  Chip,
  CircularProgress,
  Divider,
  IconButton,
  MenuItem,
  Select,
  Skeleton,
  Stack,
  Tooltip,
  Typography,
} from '@mui/material'
import DeleteOutlineIcon from '@mui/icons-material/DeleteOutline'
import PersonAddAlt1Icon from '@mui/icons-material/PersonAddAlt1'
import { useQueryClient } from '@tanstack/react-query'
import {
  getGetApiWorkspacesSlugInvitesQueryKey,
  getGetApiWorkspacesSlugMembersQueryKey,
  useDeleteApiWorkspacesSlugInvitesId,
  useDeleteApiWorkspacesSlugMembersUserId,
  useGetApiWorkspacesSlugInvites,
  useGetApiWorkspacesSlugMembers,
  usePutApiWorkspacesSlugMembersUserId,
  WorkspaceRole,
} from '../../../../../api/queries-commands'
import type {
  ProblemDetails,
  WorkspaceInviteItem,
  WorkspaceMemberItem,
} from '../../../../../api/queries-commands'
import { useNotification } from '../../../../shared/contexts/NotificationContext'
import { useWorkspace } from '../../../../shared/contexts/WorkspaceContext'
import { formatSwedishDateTime } from '../../../../shared/utils'
import { InviteDialog } from '../../members/components/InviteDialog'
import {
  bodySx,
  captionSx,
  pageCardFlushSx,
  sectionTitleSx,
  workspaceFontFamily,
  workspaceText,
} from '../../../shared'

function canManage(role: WorkspaceRole | undefined | null): boolean {
  return role === WorkspaceRole.Owner || role === WorkspaceRole.Admin
}

function readErrorDetail(err: unknown): string | null {
  const maybe = err as { response?: { data?: ProblemDetails } } | undefined
  return maybe?.response?.data?.detail ?? maybe?.response?.data?.title ?? null
}

/**
 * Workspace members + pending-invites drawer tab. Port of the routed
 * {@code MembersPage}, with the MUI DataGrid replaced by a quieter hairline
 * row stack so the surface feels less admin / more conversational.
 */
export function MembersTab() {
  const { currentWorkspace, currentSlug } = useWorkspace()
  const slug = currentSlug ?? ''
  const viewerRole: WorkspaceRole | null =
    (currentWorkspace?.role as WorkspaceRole | undefined) ?? null
  const isManager = canManage(viewerRole)
  const isOwnerViewer = viewerRole === WorkspaceRole.Owner

  const [inviteOpen, setInviteOpen] = useState(false)

  const membersQuery = useGetApiWorkspacesSlugMembers(slug, {
    query: { enabled: !!slug },
  })
  const invitesQuery = useGetApiWorkspacesSlugInvites(slug, {
    query: { enabled: !!slug && isManager },
  })

  if (!slug) return <Alert severity="error">No workspace selected.</Alert>

  const members = membersQuery.data ?? []
  const invites = invitesQuery.data ?? []

  return (
    <Stack spacing={4}>
      <Box>
        <Typography
          component="h3"
          sx={{
            fontSize: '1.25rem',
            fontWeight: 400,
            letterSpacing: '-0.01em',
            color: workspaceText.primary,
            mb: 0.5,
          }}
        >
          Members
        </Typography>
        <Typography sx={bodySx}>
          Everyone with access to this workspace and any pending invites.
        </Typography>
      </Box>

      {/* Members section */}
      <Box sx={pageCardFlushSx}>
        <Stack
          direction="row"
          alignItems="center"
          justifyContent="space-between"
          sx={{
            px: { xs: 2.5, md: 3 },
            py: 2,
            borderBottom: 1,
            borderColor: 'instrument.hairline',
          }}
        >
          <Box>
            <Typography sx={sectionTitleSx}>
              {members.length} {members.length === 1 ? 'member' : 'members'}
            </Typography>
            <Typography sx={{ ...captionSx, mt: 0.25 }}>
              Owners and admins can change roles or remove members.
            </Typography>
          </Box>
          {isManager && (
            <Button
              variant="pill" color="primary"
              startIcon={<PersonAddAlt1Icon sx={{ fontSize: 18 }} />}
              onClick={() => setInviteOpen(true)}
            >
              Invite
            </Button>
          )}
        </Stack>

        {membersQuery.isLoading ? (
          <Box sx={{ p: 2 }}>
            {Array.from({ length: 3 }).map((_, idx) => (
              <Skeleton key={idx} variant="rounded" height={56} sx={{ mb: 1 }} />
            ))}
          </Box>
        ) : membersQuery.isError ? (
          <Box sx={{ p: 3 }}>
            <Alert
              severity="error"
              variant="quiet"
            >
              Could not load members.
            </Alert>
          </Box>
        ) : members.length === 0 ? (
          <Box sx={{ p: 4 }}>
            <Typography sx={captionSx}>No members yet.</Typography>
          </Box>
        ) : (
          <Stack divider={<Divider />}>
            {members.map((member) => (
              <MemberRow
                key={member.userId}
                slug={slug}
                member={member}
                allMembers={members}
                isOwnerViewer={isOwnerViewer}
                isManager={isManager}
              />
            ))}
          </Stack>
        )}
      </Box>

      {/* Invites section */}
      {isManager && (
        <Box sx={pageCardFlushSx}>
          <Box
            sx={{
              px: { xs: 2.5, md: 3 },
              py: 2,
              borderBottom: 1,
            borderColor: 'instrument.hairline',
            }}
          >
            <Typography sx={sectionTitleSx}>Pending invites</Typography>
            <Typography sx={{ ...captionSx, mt: 0.25 }}>
              Invites that haven&rsquo;t been accepted yet.
            </Typography>
          </Box>
          {invitesQuery.isLoading ? (
            <Box sx={{ p: 3, display: 'flex', justifyContent: 'center' }}>
              <CircularProgress size={20} />
            </Box>
          ) : invites.length === 0 ? (
            <Box sx={{ p: 4 }}>
              <Typography sx={captionSx}>No pending invites.</Typography>
            </Box>
          ) : (
            <Stack
              divider={<Divider />}
            >
              {invites.map((invite) => (
                <InviteRow key={invite.id} slug={slug} invite={invite} />
              ))}
            </Stack>
          )}
        </Box>
      )}

      <InviteDialog
        open={inviteOpen}
        onClose={() => setInviteOpen(false)}
        slug={slug}
        canInviteOwner={isOwnerViewer}
      />
    </Stack>
  )
}

interface MemberRowProps {
  slug: string
  member: WorkspaceMemberItem
  allMembers: WorkspaceMemberItem[]
  isOwnerViewer: boolean
  isManager: boolean
}

function MemberRow({ slug, member, allMembers, isOwnerViewer, isManager }: MemberRowProps) {
  const queryClient = useQueryClient()
  const { showSuccess, showError } = useNotification()

  const changeRole = usePutApiWorkspacesSlugMembersUserId()
  const removeMember = useDeleteApiWorkspacesSlugMembersUserId()

  const ownerCount = useMemo(
    () => allMembers.filter((m) => m.role === WorkspaceRole.Owner).length,
    [allMembers],
  )
  const isOnlyOwner = member.role === WorkspaceRole.Owner && ownerCount <= 1

  const handleChangeRole = (nextRole: WorkspaceRole) => {
    if (member.role === nextRole) return
    if (
      member.role === WorkspaceRole.Owner &&
      ownerCount <= 1 &&
      nextRole !== WorkspaceRole.Owner
    ) {
      showError('A workspace must have at least one owner.')
      return
    }
    changeRole.mutate(
      { slug, userId: member.userId, data: { role: nextRole } },
      {
        onSuccess: () => {
          showSuccess(`Updated ${member.email} to ${nextRole}.`)
          queryClient.invalidateQueries({
            queryKey: getGetApiWorkspacesSlugMembersQueryKey(slug),
          })
        },
        onError: (err) =>
          showError(readErrorDetail(err) ?? 'Could not update the role.'),
      },
    )
  }

  const handleRemove = () => {
    if (isOnlyOwner) {
      showError('You cannot remove the only owner of the workspace.')
      return
    }
    removeMember.mutate(
      { slug, userId: member.userId },
      {
        onSuccess: () => {
          showSuccess(`Removed ${member.email}.`)
          queryClient.invalidateQueries({
            queryKey: getGetApiWorkspacesSlugMembersQueryKey(slug),
          })
        },
        onError: (err) =>
          showError(readErrorDetail(err) ?? 'Could not remove the member.'),
      },
    )
  }

  const initials = member.email.charAt(0).toUpperCase()

  return (
    <Box
      sx={{
        px: { xs: 2.5, md: 3 },
        py: 1.75,
        display: 'flex',
        alignItems: 'center',
        gap: 2,
        '&:hover': { backgroundColor: 'instrument.chipBg' },
      }}
    >
      <Avatar
        sx={{
          width: 32,
          height: 32,
          fontSize: '0.8125rem',
          backgroundColor: 'instrument.chrome',
          color: workspaceText.muted,
          border: 1,
          borderColor: 'instrument.hairline',
        }}
      >
        {initials}
      </Avatar>
      <Box sx={{ flex: 1, minWidth: 0 }}>
        <Typography
          sx={{
            fontFamily: workspaceFontFamily.sans,
            fontSize: '0.875rem',
            color: workspaceText.primary,
            fontWeight: 500,
            overflow: 'hidden',
            textOverflow: 'ellipsis',
            whiteSpace: 'nowrap',
          }}
          title={member.email}
        >
          {member.email}
        </Typography>
        <Typography sx={{ ...captionSx, fontSize: '0.75rem', mt: 0.125 }}>
          Joined {formatSwedishDateTime(member.joinedAt)}
        </Typography>
      </Box>

      {isManager ? (
        <Select
          size="small"
          value={member.role}
          onChange={(e) => handleChangeRole(e.target.value as WorkspaceRole)}
          disabled={changeRole.isPending || isOnlyOwner}
          inputProps={{ 'aria-label': `Role for ${member.email}` }}
          sx={{
            minWidth: 110,
            fontFamily: workspaceFontFamily.sans,
            fontSize: '0.8125rem',
          }}
        >
          {(isOwnerViewer || member.role === WorkspaceRole.Owner) && (
            <MenuItem value={WorkspaceRole.Owner}>Owner</MenuItem>
          )}
          <MenuItem value={WorkspaceRole.Admin}>Admin</MenuItem>
          <MenuItem value={WorkspaceRole.Member}>Member</MenuItem>
        </Select>
      ) : (
        <Chip size="small" label={member.role} variant="outlined" />
      )}

      {isManager && (
        <Tooltip title={isOnlyOwner ? 'Cannot remove the only owner' : 'Remove member'}>
          <span>
            <IconButton
              size="small"
              color="error"
              onClick={handleRemove}
              disabled={isOnlyOwner || removeMember.isPending}
              aria-label={`Remove ${member.email}`}
            >
              <DeleteOutlineIcon fontSize="small" />
            </IconButton>
          </span>
        </Tooltip>
      )}
    </Box>
  )
}

interface InviteRowProps {
  slug: string
  invite: WorkspaceInviteItem
}

function InviteRow({ slug, invite }: InviteRowProps) {
  const queryClient = useQueryClient()
  const { showSuccess, showError } = useNotification()
  const revoke = useDeleteApiWorkspacesSlugInvitesId()

  const handleRevoke = () => {
    revoke.mutate(
      { slug, id: invite.id },
      {
        onSuccess: () => {
          showSuccess(`Revoked invite for ${invite.email}.`)
          queryClient.invalidateQueries({
            queryKey: getGetApiWorkspacesSlugInvitesQueryKey(slug),
          })
        },
        onError: (err) =>
          showError(readErrorDetail(err) ?? 'Could not revoke the invite.'),
      },
    )
  }

  return (
    <Box
      sx={{
        px: { xs: 2.5, md: 3 },
        py: 1.5,
        display: 'flex',
        alignItems: 'center',
        gap: 2,
        '&:hover': { backgroundColor: 'instrument.chipBg' },
      }}
    >
      <Box sx={{ flex: 1, minWidth: 0 }}>
        <Typography
          sx={{
            fontFamily: workspaceFontFamily.sans,
            fontSize: '0.875rem',
            color: workspaceText.primary,
            overflow: 'hidden',
            textOverflow: 'ellipsis',
            whiteSpace: 'nowrap',
          }}
          title={invite.email}
        >
          {invite.email}
        </Typography>
        <Typography sx={{ ...captionSx, fontSize: '0.75rem', mt: 0.125 }}>
          Expires {formatSwedishDateTime(invite.expiresAt)}
        </Typography>
      </Box>
      <Chip size="small" label={invite.role} variant="outlined" />
      <Tooltip title="Revoke invite">
        <span>
          <IconButton
            size="small"
            color="error"
            onClick={handleRevoke}
            disabled={revoke.isPending}
            aria-label={`Revoke invite for ${invite.email}`}
          >
            <DeleteOutlineIcon fontSize="small" />
          </IconButton>
        </span>
      </Tooltip>
    </Box>
  )
}

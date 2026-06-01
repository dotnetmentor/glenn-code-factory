import { Box, Button, Dialog, DialogActions, DialogContent, DialogTitle, Snackbar, Alert, Stack, TextField, Typography } from '@mui/material'
import { useQueryClient } from '@tanstack/react-query'
import { useForm } from 'react-hook-form'
import { zodResolver } from '@hookform/resolvers/zod'
import AddIcon from '@mui/icons-material/Add'
import { useState } from 'react'
import { UsersTable } from '../components/UsersTable'
import { UserDetailsDialog } from '../components/UserDetailsDialog'
import { usePostApiUsers, getGetApiUsersQueryKey, UserListItem } from '../../../../../api/queries-commands'
import { postApiUsersBody } from '../../../../../api/zod'
import { z } from 'zod'

// Derive TypeScript type from zod schema
type CreateUserFormData = z.infer<typeof postApiUsersBody>

export function UsersPage() {
  const [createOpen, setCreateOpen] = useState(false)
  const [detailsOpen, setDetailsOpen] = useState(false)
  const [selectedUser, setSelectedUser] = useState<UserListItem | null>(null)
  const [snack, setSnack] = useState<{ open: boolean; msg: string; severity: 'success' | 'error' }>({ open: false, msg: '', severity: 'success' })
  
  const createUser = usePostApiUsers()
  const queryClient = useQueryClient()

  // Form with zod validation - uses the generated schema from orval
  const form = useForm<CreateUserFormData>({
    resolver: zodResolver(postApiUsersBody),
    defaultValues: {
      email: '',
      firstName: '',
      lastName: '',
    },
  })

  const onSubmit = form.handleSubmit((data) => {
    createUser.mutate(
      { data },
      {
        onSuccess: (response) => {
          setCreateOpen(false)
          form.reset()
          setSnack({ open: true, msg: 'User created successfully', severity: 'success' })
          queryClient.invalidateQueries({ queryKey: getGetApiUsersQueryKey(undefined) })
          
          setSelectedUser({
            id: response.userId,
            email: response.email,
            fullName: response.fullName,
            phoneNumber: null,
            roles: ['SuperAdmin'],
            createdAt: new Date().toISOString(),
          })
          setDetailsOpen(true)
        },
        onError: () => setSnack({ open: true, msg: 'Failed to create user', severity: 'error' }),
      }
    )
  })

  const handleOpenCreate = () => {
    form.reset()
    setCreateOpen(true)
  }

  const handleCloseCreate = () => {
    setCreateOpen(false)
    form.reset()
  }

  const handleEditUser = (user: UserListItem) => {
    setSelectedUser(user)
    setDetailsOpen(true)
  }

  const handleNotify = (message: string, severity: 'success' | 'error') => {
    setSnack({ open: true, msg: message, severity })
    queryClient.invalidateQueries({ queryKey: getGetApiUsersQueryKey(undefined) })
  }

  const handleCloseDetails = () => {
    setDetailsOpen(false)
    setSelectedUser(null)
  }

  return (
    <>
      <Stack spacing={5}>
            <Box sx={{ display: 'flex', justifyContent: 'space-between', alignItems: 'flex-start' }}>
              <Box>
                <Typography variant="h4" component="h1" sx={{ mb: 1 }}>
                  Users
                </Typography>
                <Typography variant="body1" color="text.secondary">
                  Explore, search, and manage users
                </Typography>
              </Box>
              <Button
                variant="contained"
                startIcon={<AddIcon />}
                onClick={handleOpenCreate}
                size="large"
                sx={{ flexShrink: 0 }}
              >
                New User
              </Button>
            </Box>

            <Box>
              <UsersTable onEditUser={handleEditUser} />
            </Box>
          </Stack>

      <Dialog open={createOpen} onClose={handleCloseCreate} maxWidth="sm" fullWidth>
        <form onSubmit={onSubmit}>
          <DialogTitle>Create User</DialogTitle>
          <DialogContent>
            <Stack spacing={2} sx={{ mt: 1 }}>
              <TextField 
                label="Email" 
                type="email" 
                required 
                autoFocus 
                fullWidth 
                error={!!form.formState.errors.email}
                helperText={form.formState.errors.email?.message}
                {...form.register('email')}
              />
              <TextField 
                label="First Name" 
                fullWidth 
                error={!!form.formState.errors.firstName}
                helperText={form.formState.errors.firstName?.message}
                {...form.register('firstName')}
              />
              <TextField 
                label="Last Name" 
                fullWidth 
                error={!!form.formState.errors.lastName}
                helperText={form.formState.errors.lastName?.message}
                {...form.register('lastName')}
              />
            </Stack>
          </DialogContent>
          <DialogActions>
            <Button type="button" onClick={handleCloseCreate}>Cancel</Button>
            <Button 
              type="submit" 
              variant="contained" 
              disabled={createUser.isPending || !form.formState.isValid}
            >
              {createUser.isPending ? 'Creating...' : 'Create'}
            </Button>
          </DialogActions>
        </form>
      </Dialog>

      <UserDetailsDialog
        open={detailsOpen}
        user={selectedUser}
        onClose={handleCloseDetails}
        onNotify={handleNotify}
      />

      <Snackbar open={snack.open} autoHideDuration={2500} onClose={() => setSnack({ ...snack, open: false })}>
        <Alert onClose={() => setSnack({ ...snack, open: false })} severity={snack.severity} variant="filled" sx={{ width: '100%' }}>
          {snack.msg}
        </Alert>
      </Snackbar>
    </>
  )
}

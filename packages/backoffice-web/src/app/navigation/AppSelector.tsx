import { Box, Card, CardActionArea, CardContent, Grid, Typography } from '@mui/material'
import { useEffect, useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { getUserApplications } from '../../applications'
import { ApplicationDefinition } from '../routing/types'
import { useAuth } from '../../auth/authContext'
import { useResolveAppPath } from '../../applications/shared/hooks/useResolveAppPath'
import { useGetApiMeWorkspaces } from '../../api/queries-commands'
import { CreateWorkspaceDialog } from '../../applications/workspace/features/workspace-settings'

interface AppCardProps {
  app: ApplicationDefinition
  /**
   * `true` when this is a slug-bound app and the user has no workspaces yet.
   * Clicking the card can't navigate anywhere useful, so it becomes the
   * "create your first workspace" recovery CTA instead of a dead no-op.
   */
  isCreateWorkspaceCta: boolean
  onCreateWorkspace: () => void
}

function AppCard({ app, isCreateWorkspaceCta, onCreateWorkspace }: AppCardProps) {
  const navigate = useNavigate()
  const { resolve } = useResolveAppPath()

  const handleClick = () => {
    if (isCreateWorkspaceCta) {
      onCreateWorkspace()
      return
    }
    const target = resolve(app.basePath)
    if (!target) return
    navigate(target)
  }

  const title = isCreateWorkspaceCta ? 'Create your first workspace' : app.name
  const description = isCreateWorkspaceCta
    ? 'You don’t have a workspace yet. Create one to start adding projects.'
    : app.description

  return (
    <Card
      elevation={0}
      sx={{
        border: '1px solid',
        borderColor: 'grey.200',
        borderRadius: 4,
        transition: 'all 0.2s ease-in-out',
        height: '100%',
        '&:hover': {
          borderColor: 'primary.main',
          transform: 'translateY(-4px)',
          boxShadow: 4
        }
      }}
    >
      <CardActionArea
        onClick={handleClick}
        sx={{ 
          height: '100%',
          display: 'flex',
          flexDirection: 'column',
          alignItems: 'stretch'
        }}
      >
        <CardContent 
          sx={{ 
            textAlign: 'center', 
            py: 6, 
            px: 4,
            flexGrow: 1,
            display: 'flex',
            flexDirection: 'column',
            justifyContent: 'center'
          }}
        >
          <Typography 
            variant="h6" 
            component="h3" 
            sx={{ 
              fontWeight: 500,
              color: 'text.primary',
              mb: 1.5,
              letterSpacing: '-0.01em'
            }}
          >
            {title}
          </Typography>

          <Typography
            variant="body2"
            sx={{
              color: 'text.secondary',
              lineHeight: 1.5,
              fontSize: '0.95rem'
            }}
          >
            {description}
          </Typography>
        </CardContent>
      </CardActionArea>
    </Card>
  )
}

export function AppSelector() {
  const auth = useAuth()
  const navigate = useNavigate()
  const { resolve } = useResolveAppPath()
  const userApplications = getUserApplications(auth.user?.roles || [])

  // Distinguish "workspaces still loading" from "loaded, none exist". A
  // slug-bound app can't resolve a target for a zero-workspace user, so
  // auto-redirecting (or rendering a plain card) just strands them — we
  // surface a create-workspace CTA instead.
  const { isLoading: workspacesLoading, data: workspacesData } = useGetApiMeWorkspaces()
  const hasNoWorkspaces = !workspacesLoading && (workspacesData?.length ?? 0) === 0
  const [createWorkspaceOpen, setCreateWorkspaceOpen] = useState(false)

  const handleWorkspaceCreated = (slug: string) => {
    navigate(`/w/${slug}`, { replace: true })
  }

  useEffect(() => {
    if (userApplications.length === 1) {
      const onlyApp = userApplications[0]
      // Don't auto-redirect into a slug-bound app the user can't resolve yet
      // (no workspaces). That navigate is a no-op and leaves them on a blank
      // screen — let the card's create-workspace CTA render instead.
      if (onlyApp.basePath.includes(':slug') && hasNoWorkspaces) return
      const target = resolve(onlyApp.basePath)
      if (!target) return
      navigate(target, { replace: true })
    }
  }, [userApplications, navigate, resolve, hasNoWorkspaces])

  if (userApplications.length === 0) {
    return (
      <Box 
        sx={{ 
          minHeight: '70vh',
          display: 'flex',
          flexDirection: 'column',
          alignItems: 'center',
          justifyContent: 'center',
          textAlign: 'center',
          px: 2
        }}
      >
        <Typography 
          variant="h3" 
          sx={{ 
            fontWeight: 300,
            color: 'text.primary',
            mb: 2,
            letterSpacing: '-0.02em'
          }}
        >
          No Applications Available
        </Typography>
        
        <Typography 
          variant="body1" 
          sx={{ 
            color: 'text.secondary',
            maxWidth: 480,
            lineHeight: 1.6,
            mb: 6,
            fontSize: '1.1rem'
          }}
        >
          You don't have access to any applications yet. Contact your administrator to get the appropriate permissions.
        </Typography>
      </Box>
    )
  }

  return (
    <Box sx={{ px: 2, py: 4 }}>
      <Box sx={{ textAlign: 'center', mb: 6 }}>
        <Typography 
          variant="h3" 
          sx={{ 
            fontWeight: 300,
            color: 'text.primary',
            mb: 2,
            letterSpacing: '-0.02em'
          }}
        >
          Select Application
        </Typography>
        <Typography 
          variant="body1" 
          sx={{ 
            color: 'text.secondary',
            fontSize: '1.1rem',
            maxWidth: 480,
            mx: 'auto',
            lineHeight: 1.6
          }}
        >
          Choose the application you want to access
        </Typography>
      </Box>
      
      <Box sx={{ maxWidth: 1200, mx: 'auto' }}>
        <Grid container spacing={4} justifyContent="center">
          {userApplications.map((app) => (
            <Grid key={app.id} size={{ xs: 12, sm: 6, md: 4, lg: 3 }}>
              <AppCard
                app={app}
                isCreateWorkspaceCta={app.basePath.includes(':slug') && hasNoWorkspaces}
                onCreateWorkspace={() => setCreateWorkspaceOpen(true)}
              />
            </Grid>
          ))}
        </Grid>
      </Box>

      <CreateWorkspaceDialog
        open={createWorkspaceOpen}
        onClose={() => setCreateWorkspaceOpen(false)}
        onCreated={handleWorkspaceCreated}
      />
    </Box>
  )
}

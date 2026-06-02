import { Navigate, useParams } from 'react-router-dom'
import {
  Alert,
  Box,
  CircularProgress,
  Stack,
  Typography,
} from '@mui/material'
import {
  useGetApiProjectsProjectIdBranches,
  type ProblemDetails,
  type ProjectBranchDto,
} from '../../../../../api/queries-commands'
import { useWorkspace } from '../../../../shared/contexts/WorkspaceContext'
import { WorkspaceShellLayout } from '../../project-workspace/components/WorkspaceShellLayout'

import { chromeTokens, surfaceTokens } from '../../../shared/designTokens'

const tokens = { ...surfaceTokens, ...chromeTokens }

function readErrorDetail(err: unknown): string | null {
  const maybe = err as { response?: { data?: ProblemDetails } } | undefined
  return maybe?.response?.data?.detail ?? maybe?.response?.data?.title ?? null
}

interface RouteParams {
  projectId: string
  [key: string]: string | undefined
}

/**
 * Intermediate route at {@code /w/:slug/projects/:projectId} that resolves
 * the project's default branch and redirects to the per-branch IDE shell.
 *
 * <p>Lets callers link to a project without already knowing a branchId — e.g.
 * sidebar project rows and the landing page when only a project id is known.</p>
 *
 * <p>This route is {@code chromeless: true} and renders its loading / error
 * states inside the same {@link WorkspaceShellLayout} as the rest of the
 * workspace surfaces — same sidebar, same paper canvas — so the half-second
 * of resolution feels like a continuation of the user's environment instead
 * of a flash of the old AppLayout chrome.</p>
 */
export function ProjectRedirectPage() {
  const { projectId = '' } = useParams<RouteParams>()
  const { currentSlug } = useWorkspace()
  const slug = currentSlug ?? ''

  const branchesQuery = useGetApiProjectsProjectIdBranches(
    projectId,
    undefined,
    {
      query: { enabled: !!projectId },
    },
  )

  if (!slug || !projectId) {
    return (
      <WorkspaceShellLayout>
        <RedirectCanvas>
          <Alert
            severity="error"
            variant="quiet"
          >
            Missing workspace or project.
          </Alert>
        </RedirectCanvas>
      </WorkspaceShellLayout>
    )
  }

  if (branchesQuery.isLoading) {
    return (
      <WorkspaceShellLayout>
        <RedirectCanvas>
          <Stack spacing={2} alignItems="center">
            <CircularProgress
              size={20}
              thickness={4}
              sx={{ color: tokens.accent }}
            />
            <Typography
              sx={{
                fontSize: '0.875rem',
                color: tokens.textMuted,
                letterSpacing: '-0.005em',
              }}
            >
              Opening project…
            </Typography>
          </Stack>
        </RedirectCanvas>
      </WorkspaceShellLayout>
    )
  }

  if (branchesQuery.isError) {
    return (
      <WorkspaceShellLayout>
        <RedirectCanvas>
          <Alert
            severity="error"
            variant="quiet"
          >
            {readErrorDetail(branchesQuery.error) ??
              'Could not load project branches.'}
          </Alert>
        </RedirectCanvas>
      </WorkspaceShellLayout>
    )
  }

  const branches: ProjectBranchDto[] = branchesQuery.data ?? []
  if (branches.length === 0) {
    return (
      <WorkspaceShellLayout>
        <RedirectCanvas>
          <Alert
            severity="warning"
            variant="quiet"
          >
            This project has no branches yet. Wait for the runtime to
            initialise it, or contact a workspace admin.
          </Alert>
        </RedirectCanvas>
      </WorkspaceShellLayout>
    )
  }

  const targetBranch = branches.find((b) => b.isDefault) ?? branches[0]
  return (
    <Navigate
      to={`/w/${slug}/projects/${projectId}/branches/${targetBranch.id}`}
      replace
    />
  )
}

/**
 * Calm centred canvas used by every state above the eventual {@code
 * <Navigate>}. Mirrors {@code NewSessionView}'s 880px paper column so the
 * "Opening project…" spinner feels like the rest of the workspace shell,
 * not a chrome-stripped error page.
 */
function RedirectCanvas({ children }: { children: React.ReactNode }) {
  return (
    <Box
      sx={{
        width: '100%',
        height: '100%',
        overflow: 'auto',
        backgroundColor: tokens.canvasBg,
        display: 'flex',
        alignItems: 'center',
        justifyContent: 'center',
      }}
    >
      <Box
        sx={{
          maxWidth: 480,
          width: '100%',
          mx: 'auto',
          px: { xs: 3, md: 4 },
          py: { xs: 4, md: 6 },
        }}
      >
        {children}
      </Box>
    </Box>
  )
}

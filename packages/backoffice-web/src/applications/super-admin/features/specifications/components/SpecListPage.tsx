import { useCallback } from 'react'
import { useNavigate, useParams } from 'react-router-dom'
import { useQueryClient } from '@tanstack/react-query'
import {
  Alert,
  Box,
  CircularProgress,
  Container,
  Paper,
  Stack,
  Table,
  TableBody,
  TableCell,
  TableContainer,
  TableHead,
  TableRow,
  Typography,
} from '@mui/material'
import DescriptionOutlinedIcon from '@mui/icons-material/DescriptionOutlined'
import {
  getGetApiProjectsProjectIdSpecificationsQueryKey,
  useGetApiProjectsProjectIdSpecifications,
  type SpecificationSummaryDto,
} from '@/api/queries-commands'
import { formatSwedishDateTime } from '@/applications/shared/utils'
import { usePlanningSignalR } from '../hooks/usePlanningSignalR'

interface SpecListPageProps {
  /**
   * When supplied (e.g. embedded inside the workspace AppContainer's
   * Specs tab), the page reads its project id from props rather than the
   * URL. Falls back to <c>useParams</c> so the standalone super-admin
   * route keeps working unchanged.
   */
  projectId?: string
  /**
   * When provided, a row click calls this callback with the spec slug
   * instead of navigating. Used by the workspace tab to switch into its
   * own in-tab detail view without leaving the IDE shell.
   */
  onSelectSpec?: (slug: string) => void
  /**
   * When {@code true} the page suppresses its own title block + page
   * gutter — the surrounding workspace SecondaryHeader is already
   * labelled "Specifications" and the AppContainer owns the outer
   * padding. Defaults to {@code false} so the standalone /specs route
   * keeps its current standalone look.
   */
  embedded?: boolean
}

/**
 * Project-scoped list of {@link SpecificationSummaryDto} — the agent owns
 * spec lifecycle through MCP, so this surface is read-only. The page mounts
 * the planning SignalR hook with an <c>onSpecificationChanged</c> handler
 * that invalidates the list query key, so create / update / delete by the
 * agent appears live without polling.
 *
 * <p>Routes:
 * <c>/super-admin/projects/:projectId/specs</c>
 * → row click → <c>/super-admin/projects/:projectId/specs/:slug</c>.</p>
 *
 * <p>Also reusable inside the workspace IDE's Specs tab — pass
 * <c>projectId</c> + <c>onSelectSpec</c> to override the URL params and
 * the navigation side-effect.</p>
 */
export function SpecListPage({
  projectId: projectIdProp,
  onSelectSpec,
  embedded = false,
}: SpecListPageProps = {}) {
  const params = useParams<{ projectId: string }>()
  const projectId = projectIdProp ?? params.projectId ?? ''
  const navigate = useNavigate()
  const queryClient = useQueryClient()

  const query = useGetApiProjectsProjectIdSpecifications(projectId, {
    query: { enabled: !!projectId },
  })

  const onSpecificationChanged = useCallback(() => {
    queryClient.invalidateQueries({
      queryKey: getGetApiProjectsProjectIdSpecificationsQueryKey(projectId),
    })
  }, [projectId, queryClient])

  usePlanningSignalR(projectId || undefined, { onSpecificationChanged })

  if (!projectId) {
    return (
      <Container maxWidth="md" sx={{ py: 4 }}>
        <Alert severity="error">No project id in URL.</Alert>
      </Container>
    )
  }

  const specs: SpecificationSummaryDto[] = query.data ?? []

  return (
    <Container
      maxWidth={embedded ? false : 'lg'}
      disableGutters={embedded}
      sx={{
        py: embedded ? 2 : 4,
        // See KanbanBoardPage for the same trick — when embedded inside the
        // workspace AppContainer the SecondaryHeader already labels the
        // surface and the parent owns the outer gutter, so we collapse to a
        // tight 16px x-pad.
        px: embedded ? 2 : undefined,
      }}
    >
      <Stack spacing={embedded ? 2 : 4}>
            {!embedded && (
              <Box>
                <Typography variant="h4" component="h1" sx={{ mb: 1 }}>
                  Specifications
                </Typography>
                <Typography variant="body1" color="text.secondary">
                  Specs the agent has drafted for this project. Open one to read
                  the full requirements.
                </Typography>
              </Box>
            )}

            {query.isPending ? (
              <Stack direction="row" spacing={1.5} alignItems="center">
                <CircularProgress size={16} />
                <Typography variant="body2" color="text.secondary">
                  Loading specifications…
                </Typography>
              </Stack>
            ) : query.error ? (
              <Alert severity="error">
                Failed to load specifications. Try refreshing the page.
              </Alert>
            ) : specs.length === 0 ? (
              <Paper
                variant="outlined"
                sx={{
                  p: 6,
                  textAlign: 'center',
                  borderStyle: 'dashed',
                }}
              >
                <Stack spacing={1.5} alignItems="center">
                  <DescriptionOutlinedIcon
                    sx={{ fontSize: 48, color: 'text.disabled' }}
                  />
                  <Typography variant="h6" color="text.primary">
                    No specifications yet
                  </Typography>
                  <Typography
                    variant="body2"
                    color="text.secondary"
                    sx={{ maxWidth: 420 }}
                  >
                    The agent will draft one here when you ask it to build
                    something — say what you want and a spec appears.
                  </Typography>
                </Stack>
              </Paper>
            ) : (
              <TableContainer component={Paper} variant="outlined">
                <Table>
                  <TableHead>
                    <TableRow>
                      <TableCell>Name</TableCell>
                      <TableCell>Slug</TableCell>
                      <TableCell>Updated</TableCell>
                    </TableRow>
                  </TableHead>
                  <TableBody>
                    {specs.map((spec) => (
                      <TableRow
                        key={spec.id}
                        hover
                        onClick={() => {
                          if (onSelectSpec) {
                            onSelectSpec(spec.slug)
                          } else {
                            navigate(
                              `/super-admin/projects/${projectId}/specs/${spec.slug}`,
                            )
                          }
                        }}
                        sx={{ cursor: 'pointer' }}
                      >
                        <TableCell>
                          <Typography variant="body2" sx={{ fontWeight: 500 }}>
                            {spec.name}
                          </Typography>
                        </TableCell>
                        <TableCell>
                          <Typography
                            variant="body2"
                            sx={{
                              fontFamily:
                                '"JetBrains Mono", "Fira Code", monospace',
                              fontSize: '0.8125rem',
                              color: 'text.secondary',
                            }}
                          >
                            {spec.slug}
                          </Typography>
                        </TableCell>
                        <TableCell>
                          <Typography variant="body2" color="text.secondary">
                            {formatSwedishDateTime(spec.updatedAt) || '—'}
                          </Typography>
                        </TableCell>
                      </TableRow>
                    ))}
                  </TableBody>
                </Table>
              </TableContainer>
            )}
          </Stack>
    </Container>
  )
}

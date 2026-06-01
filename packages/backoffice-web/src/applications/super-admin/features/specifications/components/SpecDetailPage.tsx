import { useCallback, useState } from 'react'
import { useNavigate, useParams } from 'react-router-dom'
import { useQueryClient } from '@tanstack/react-query'
import {
  Alert,
  Box,
  Button,
  Chip,
  CircularProgress,
  Container,
  Divider,
  Paper,
  Snackbar,
  Stack,
  Typography,
} from '@mui/material'
import ArrowBackIcon from '@mui/icons-material/ArrowBack'
import {
  getGetApiProjectsProjectIdSpecificationsQueryKey,
  getGetApiProjectsProjectIdSpecificationsSlugQueryKey,
  useGetApiProjectsProjectIdSpecificationsSlug,
} from '@/api/queries-commands'
import { formatSwedishDateTime } from '@/applications/shared/utils'
import { PlanningChangeKind } from '@/generated/signalr/Source.Features.SignalR.Contracts'
import { MarkdownContent } from '@/applications/shared/components/MarkdownContent'
import { usePlanningSignalR } from '../hooks/usePlanningSignalR'

interface SpecDetailPageProps {
  /**
   * Overrides the URL <c>projectId</c> param. Used when the page is
   * embedded inside the workspace AppContainer's Specs tab.
   */
  projectId?: string
  /**
   * Overrides the URL <c>slug</c> param. When supplied (with a non-null
   * value) the page renders that spec; when explicitly <c>null</c> the
   * caller is in list mode and the page shouldn't be rendered at all.
   */
  slug?: string
  /**
   * Replaces the default "back to list" navigation. When provided, the
   * back button — and the post-delete redirect — calls this callback
   * instead of navigating via react-router.
   */
  onBack?: () => void
  /**
   * When {@code true} the page suppresses its own back button (the parent
   * Specs tab promotes it into the SecondaryHeader's right slot) and
   * collapses its outer gutter. Defaults to {@code false} so the
   * standalone super-admin route keeps the full page chrome.
   */
  embedded?: boolean
}

/**
 * Full read view of a single {@link SpecificationDto}. Loads via
 * <c>GET /api/projects/{projectId}/specifications/{slug}</c> (Orval) and
 * renders the markdown <c>content</c> through {@link MarkdownContent} so the
 * typography matches the analytics-chat surface already shipping markdown
 * in this app.
 *
 * <p>Realtime: the planning SignalR hook is mounted with an
 * <c>onSpecificationChanged</c> callback that:
 * <ul>
 *   <li>Invalidates the list query (so a back-nav shows the latest list).</li>
 *   <li>Invalidates the detail query when the slug matches.</li>
 *   <li>On <c>Deleted</c> (any slug — the agent might have deleted *this*
 *       one): navigate back to the list with a snackbar explaining why.</li>
 * </ul>
 * Per-project group filtering happens inside {@link usePlanningSignalR}.</p>
 *
 * <p>Also reusable inside the workspace IDE's Specs tab — pass
 * <c>projectId</c>, <c>slug</c>, and <c>onBack</c> to override the URL
 * params and the back-navigation side-effect.</p>
 */
export function SpecDetailPage({
  projectId: projectIdProp,
  slug: slugProp,
  onBack,
  embedded = false,
}: SpecDetailPageProps = {}) {
  const params = useParams<{
    projectId: string
    slug: string
  }>()
  const projectId = projectIdProp ?? params.projectId ?? ''
  const slug = slugProp ?? params.slug ?? ''
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const [deletedSnackbarOpen, setDeletedSnackbarOpen] = useState(false)

  const goBack = useCallback(() => {
    if (onBack) {
      onBack()
    } else {
      navigate(`/super-admin/projects/${projectId}/specs`)
    }
  }, [onBack, navigate, projectId])

  const query = useGetApiProjectsProjectIdSpecificationsSlug(projectId, slug, {
    query: { enabled: !!projectId && !!slug },
  })

  const onSpecificationChanged = useCallback(
    (payload: {
      kind: PlanningChangeKind
      slug: string
    }) => {
      // Refresh the list cache regardless — any change in the project means
      // the list view is stale.
      queryClient.invalidateQueries({
        queryKey: getGetApiProjectsProjectIdSpecificationsQueryKey(projectId),
      })

      // Only the matching slug needs a detail refetch — other specs in the
      // project don't share this query key.
      if (payload.slug === slug) {
        queryClient.invalidateQueries({
          queryKey: getGetApiProjectsProjectIdSpecificationsSlugQueryKey(
            projectId,
            slug,
          ),
        })

        if (payload.kind === PlanningChangeKind.Deleted) {
          setDeletedSnackbarOpen(true)
          // Brief delay so the user reads the toast before navigation tears
          // it down.
          setTimeout(() => {
            goBack()
          }, 1500)
        }
      }
    },
    [projectId, slug, queryClient, goBack],
  )

  usePlanningSignalR(projectId || undefined, { onSpecificationChanged })

  if (!projectId || !slug) {
    return (
      <Container maxWidth="md" sx={{ py: 4 }}>
        <Alert severity="error">Missing project id or spec slug in URL.</Alert>
      </Container>
    )
  }

  const spec = query.data

  return (
    <Container
      maxWidth={embedded ? false : 'lg'}
      disableGutters={embedded}
      sx={{
        py: embedded ? 2 : 4,
        // Embedded inside the workspace AppContainer: the SecondaryHeader
        // owns the back affordance (in its right slot) and the parent
        // column owns the outer gutter — so we shed the inline back
        // button and tighten the page x-pad to 16px.
        px: embedded ? 2 : undefined,
      }}
    >
      <Stack spacing={2}>
        {!embedded && (
          <Box>
            <Button
              startIcon={<ArrowBackIcon />}
              onClick={goBack}
              size="small"
            >
              Back to specifications
            </Button>
          </Box>
        )}

        {query.isPending ? (
          <Stack direction="row" spacing={1.5} alignItems="center">
            <CircularProgress size={16} />
            <Typography variant="body2" color="text.secondary">
              Loading specification…
            </Typography>
          </Stack>
        ) : query.error ? (
          <Alert severity="error">
            Failed to load specification. It might have been deleted, or you
            don't have access.
          </Alert>
        ) : spec ? (
          <Stack spacing={3}>
                <Box>
                  <Typography variant="h4" component="h1" sx={{ mb: 1 }}>
                    {spec.name}
                  </Typography>
                  <Stack
                    direction="row"
                    spacing={1.5}
                    alignItems="center"
                    flexWrap="wrap"
                    sx={{ rowGap: 1 }}
                  >
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
                    <Chip
                      size="small"
                      label={String(spec.status)}
                      color="default"
                      variant="outlined"
                    />
                    <Typography variant="caption" color="text.secondary">
                      Updated {formatSwedishDateTime(spec.updatedAt) || '—'}
                    </Typography>
                  </Stack>
                </Box>

                <Paper
                  variant="outlined"
                  sx={{
                    px: 2,
                    py: 1.5,
                    bgcolor: 'background.default',
                  }}
                >
                  <Stack
                    direction="row"
                    spacing={3}
                    flexWrap="wrap"
                    sx={{ rowGap: 1 }}
                  >
                    <MetaItem label="Slug" value={spec.slug} mono />
                    <MetaItem label="Status" value={String(spec.status)} />
                    <MetaItem
                      label="Created"
                      value={formatSwedishDateTime(spec.createdAt) || '—'}
                    />
                    <MetaItem
                      label="Updated"
                      value={formatSwedishDateTime(spec.updatedAt) || '—'}
                    />
                  </Stack>
                </Paper>

                <Divider />

                <Box>
                  {spec.content?.trim() ? (
                    <MarkdownContent content={spec.content} />
                  ) : (
                    <Typography variant="body2" color="text.secondary">
                      This specification has no content yet.
                    </Typography>
                  )}
                </Box>
              </Stack>
        ) : null}
      </Stack>

      <Snackbar
        open={deletedSnackbarOpen}
        autoHideDuration={2000}
        onClose={() => setDeletedSnackbarOpen(false)}
        anchorOrigin={{ vertical: 'bottom', horizontal: 'center' }}
      >
        <Alert severity="info" variant="filled" sx={{ width: '100%' }}>
          Spec was deleted — returning to the list.
        </Alert>
      </Snackbar>
    </Container>
  )
}

interface MetaItemProps {
  label: string
  value: string
  mono?: boolean
}

function MetaItem({ label, value, mono }: MetaItemProps) {
  return (
    <Box>
      <Typography
        variant="caption"
        color="text.secondary"
        sx={{
          display: 'block',
          textTransform: 'uppercase',
          fontSize: '0.6875rem',
          letterSpacing: '0.05em',
          mb: 0.25,
        }}
      >
        {label}
      </Typography>
      <Typography
        variant="body2"
        sx={
          mono
            ? {
                fontFamily: '"JetBrains Mono", "Fira Code", monospace',
                fontSize: '0.8125rem',
              }
            : undefined
        }
      >
        {value}
      </Typography>
    </Box>
  )
}

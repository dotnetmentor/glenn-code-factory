import { useEffect, useState } from 'react'
import {
  Box,
  Button,
  Stack,
  TextField,
  Typography,
} from '@mui/material'
import { useQueryClient } from '@tanstack/react-query'
import {
  getGetApiProjectsProjectIdQueryKey,
  useGetApiProjectsProjectId,
  usePatchApiProjectsProjectIdPreviewPort,
  type UpdateProjectPreviewPortResponse,
} from '../../../../../api/queries-commands'
import { useNotification } from '../../../../shared/contexts/NotificationContext'
import {
  bodySx,
  captionSx,
  sectionTitleSx,
  workspaceColors,
  workspaceFontFamily,
  workspaceText,
} from '../../../shared'

const DEFAULT_PREVIEW_PORT = 5173
const MIN_PREVIEW_PORT = 1
const MAX_PREVIEW_PORT = 65535

interface PreviewSettingsTabProps {
  projectId: string
}

/**
 * Preview-port settings tab — sits next to "Runtime" in the project settings
 * drawer. The runtime tab owns the runtime spec; this tab owns the realtime
 * Cloudflare tunnel port the preview iframe points at.
 *
 * <p>Saving fires PATCH /api/projects/{projectId}/preview-port, which the
 * backend uses to (1) update the DB row, (2) re-point every branch's
 * Cloudflare ingress at the new port, and (3) fan a {@code PreviewPortChanged}
 * SignalR event out to every open workspace tab. The AppContainer iframe
 * picks the event up and reloads against the new port without any restart.</p>
 *
 * <p>Behavior:
 * <ul>
 *   <li>Pre-fills from {@code projectQuery.data.previewPort}.</li>
 *   <li>Save is disabled when the value matches the current port or is
 *       out of range.</li>
 *   <li>No optimistic iframe refresh on click — the SignalR event drives
 *       the reload so the user sees the iframe flip only when the edge
 *       has actually accepted the change.</li>
 *   <li>If any branch tunnel update fails partway, the snackbar shifts to
 *       {@code warning} so the user knows some branches need attention.</li>
 * </ul></p>
 */
export function PreviewSettingsTab({ projectId }: PreviewSettingsTabProps) {
  const queryClient = useQueryClient()
  const { showSuccess, showWarning, showError } = useNotification()

  const projectQuery = useGetApiProjectsProjectId(projectId, {
    query: { enabled: !!projectId },
  })
  const currentPreviewPort = projectQuery.data?.previewPort ?? DEFAULT_PREVIEW_PORT

  const [draft, setDraft] = useState<string>(String(currentPreviewPort))
  useEffect(() => {
    setDraft(String(currentPreviewPort))
  }, [currentPreviewPort])

  const mutation = usePatchApiProjectsProjectIdPreviewPort()

  const parsed = Number(draft)
  const isInteger = draft.trim() !== '' && Number.isInteger(parsed)
  const inRange = isInteger && parsed >= MIN_PREVIEW_PORT && parsed <= MAX_PREVIEW_PORT
  const dirty = inRange && parsed !== currentPreviewPort
  const hasFieldError = draft.trim() !== '' && !inRange
  const canSave = dirty && !mutation.isPending

  // Clamp on blur so the visible value never strays into invalid territory
  // even if the user typed something out-of-bounds. We don't clamp on every
  // keystroke — that would fight the user mid-edit.
  const handleBlur = () => {
    if (draft.trim() === '') return
    const n = Number(draft)
    if (!Number.isFinite(n)) return
    const clamped = Math.max(MIN_PREVIEW_PORT, Math.min(MAX_PREVIEW_PORT, Math.round(n)))
    if (String(clamped) !== draft) {
      setDraft(String(clamped))
    }
  }

  const handleSave = () => {
    if (!canSave) return
    mutation.mutate(
      { projectId, data: { port: parsed } },
      {
        onSuccess: (response: UpdateProjectPreviewPortResponse) => {
          const failed = response.tunnelsFailed ?? 0
          const updated = response.tunnelsUpdated ?? 0
          if (failed > 0) {
            showWarning(
              `Preview port updated to ${response.port}. ${updated} of ${
                updated + failed
              } tunnels were re-pointed — the rest will retry on the next branch boot.`,
            )
          } else {
            showSuccess(
              `Preview port updated to ${response.port}. Make sure your dev server is listening on this port.`,
            )
          }
          queryClient.invalidateQueries({
            queryKey: getGetApiProjectsProjectIdQueryKey(projectId),
          })
        },
        onError: (error: unknown) => {
          const errorBody = (error as { response?: { data?: { detail?: string; error?: string } } })
            ?.response?.data
          const code = errorBody?.detail ?? errorBody?.error
          if (code === 'invalid_preview_port') {
            // Inline helper already covers visual error; surface a snackbar
            // too so the user gets a hit either way.
            showError(`Preview port must be between ${MIN_PREVIEW_PORT} and ${MAX_PREVIEW_PORT}.`)
          } else {
            showError('Could not update the preview port.')
          }
        },
      },
    )
  }

  return (
    <Stack spacing={4}>
      {/* Heading */}
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
          Preview
        </Typography>
        <Typography sx={bodySx}>
          The port your app listens on. Updates every branch's preview tunnel
          live — no restart needed.
        </Typography>
      </Box>

      {/* Preview port */}
      <Box
        sx={{
          border: `1px solid ${workspaceColors.hairline}`,
          borderRadius: 2,
          p: { xs: 2.5, md: 3 },
        }}
      >
        <Stack spacing={2}>
          <Box>
            <Typography sx={sectionTitleSx}>Port your app listens on</Typography>
            <Typography sx={{ ...captionSx, mt: 0.5 }}>
              The preview tunnel will route to {`localhost:{port}`} on your
              runtime. Your dev server must be listening on this port (e.g.
              Vite default 5173, Next.js 3000).
            </Typography>
          </Box>
          <Stack direction="row" spacing={1.5} alignItems="flex-start">
            <TextField
              value={draft}
              onChange={(e) => setDraft(e.target.value)}
              onBlur={handleBlur}
              size="small"
              type="number"
              error={hasFieldError}
              helperText={
                hasFieldError
                  ? `Enter a port between ${MIN_PREVIEW_PORT} and ${MAX_PREVIEW_PORT}.`
                  : undefined
              }
              inputProps={{
                'aria-label': 'Preview port',
                min: MIN_PREVIEW_PORT,
                max: MAX_PREVIEW_PORT,
                step: 1,
                inputMode: 'numeric',
              }}
              InputProps={{
                sx: {
                  backgroundColor: workspaceColors.inputBg,
                  fontFamily: workspaceFontFamily.sans,
                  fontSize: '0.875rem',
                  color: workspaceText.primary,
                },
              }}
              sx={{ width: 140 }}
            />
            <Button
              variant="pill" color="primary"
              onClick={handleSave}
              disabled={!canSave}
            >
              {mutation.isPending ? 'Saving…' : 'Save'}
            </Button>
          </Stack>
          <Typography sx={{ ...captionSx, mt: 0.5 }}>
            Current port: {currentPreviewPort}
          </Typography>
        </Stack>
      </Box>
    </Stack>
  )
}

import { useCallback, useState } from 'react'
import {
  Box,
  CircularProgress,
  Paper,
  Stack,
  Tooltip,
  Typography,
} from '@mui/material'
import DescriptionIcon from '@mui/icons-material/Description'
import OpenInNewIcon from '@mui/icons-material/OpenInNew'
import { getApiAttachmentsId } from '../../../../../api/queries-commands'

/**
 * Props for {@link PastAttachmentChip} — the static, clickable representation
 * of an attachment that was sent on a previous turn (already-sent message in
 * chat history).
 *
 * <p>chat-file-attachments — Scene 5: "A week later, Maria scrolls back to the
 * conversation where she shared the vendor proposal. Her original message
 * still shows the attachment chip. She clicks it and the file opens in a new
 * browser tab — because it's a PDF, the browser previews it inline."</p>
 */
export interface PastAttachmentChipProps {
  attachmentId: string
  fileName: string
  sizeBytes: number
  contentType?: string | null
}

/** Format bytes as B / KB / MB with one decimal. Mirrors {@link AttachmentChip}. */
function formatSize(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`
  return `${(bytes / 1024 / 1024).toFixed(1)} MB`
}

/**
 * Past-message attachment chip. Renders a small, neutral, clickable pill with
 * a file icon, the filename, its size, and a subtle "open in new tab" hint.
 *
 * <p><b>Click behaviour.</b> We don't bake a presigned download URL into the
 * chat-history payload — those URLs expire (~24h TTL) so embedding them would
 * silently rot weeks-old conversations. Instead, on click we fetch a fresh
 * presigned URL via <c>GET /api/attachments/{id}</c> and open it in a new tab
 * using <c>window.open(downloadUrl, '_blank')</c>. The browser then handles
 * rendering natively — PDFs / images / text preview inline; unknown binary
 * types fall back to a download. No bespoke in-app previewer (explicit
 * non-goal in the spec).</p>
 *
 * <p><b>Why not eager-fetch on mount.</b> A conversation can carry many
 * attachments across its history; fanning out one <c>GET /api/attachments/:id</c>
 * call per chip on transcript hydration would balloon the wire and waste
 * presign work the user will never trigger (most chips never get clicked).
 * Lazy-on-click keeps the history load cheap.</p>
 *
 * <p><b>Styling.</b> Mirrors the live <see cref="AttachmentChip"/> "ready"
 * state but slightly more muted (no success-green accent) so it reads as a
 * static historical record rather than an active composer chip. The small
 * {@code OpenInNew} glyph on the right is the visual affordance that the chip
 * opens externally — kept tiny so the chip stays calm in a scrolled
 * transcript.</p>
 */
export function PastAttachmentChip(props: PastAttachmentChipProps) {
  const { attachmentId, fileName, sizeBytes, contentType } = props

  // Loading state covers the brief window between click and window.open. Most
  // presigns return in <200ms but we still surface a spinner so a slow network
  // doesn't look like a dead click. Disabled re-entry while loading prevents a
  // user from triggering N parallel presigns on a tap-happy device.
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const handleClick = useCallback(
    async (e: React.MouseEvent<HTMLAnchorElement>) => {
      // Always preventDefault — the anchor has no real href (no presigned URL
      // baked into history); we fetch one on demand and route via window.open.
      e.preventDefault()
      if (loading) return
      setLoading(true)
      setError(null)
      try {
        const detail = await getApiAttachmentsId(attachmentId)
        // Open in a new browser tab so the browser's native handler renders
        // PDFs / images / text inline and falls back to a download for unknown
        // binary types. {@code noopener,noreferrer} are best-practice for any
        // user-driven cross-origin open.
        const w = window.open(detail.downloadUrl, '_blank', 'noopener,noreferrer')
        if (!w) {
          // Pop-up blocked — surface a quiet error so the user knows their
          // click did register. We don't try to fall back to a same-tab nav
          // because that would tear them out of the chat.
          setError('Pop-up blocked. Allow pop-ups for this site to open attachments.')
        }
      } catch (err) {
        // eslint-disable-next-line no-console
        console.warn('[PastAttachmentChip] failed to fetch download url', err)
        setError(
          err instanceof Error
            ? err.message
            : 'Failed to open attachment.',
        )
      } finally {
        setLoading(false)
      }
    },
    [attachmentId, loading],
  )

  // The tooltip on the anchor shows full filename (chip ellipsis truncates
  // long names) plus the content type when known so power users can verify
  // what they're about to open without a click.
  const tooltipTitle = contentType
    ? `${fileName} (${contentType})`
    : fileName

  return (
    <Tooltip title={error ?? tooltipTitle} placement="top" enterDelay={400}>
      <Paper
        component="a"
        href="#"
        onClick={handleClick}
        variant="outlined"
        aria-label={`Open ${fileName} in a new tab`}
        sx={{
          display: 'inline-flex',
          alignItems: 'center',
          gap: 1,
          px: 1,
          py: 0.625,
          borderRadius: 1.5,
          borderColor: error ? 'error.main' : 'divider',
          // Slightly muted background so the chip reads as a historical /
          // static record rather than an active composer chip. Hover lifts it
          // subtly to telegraph the click target without shouting.
          bgcolor: 'background.paper',
          color: 'text.primary',
          textDecoration: 'none',
          cursor: loading ? 'progress' : 'pointer',
          maxWidth: 320,
          minWidth: 0,
          overflow: 'hidden',
          transition: 'background-color 120ms ease, border-color 120ms ease',
          '&:hover': {
            bgcolor: 'action.hover',
            borderColor: 'text.disabled',
          },
          '&:focus-visible': {
            outline: (theme) => `2px solid ${theme.palette.primary.main}`,
            outlineOffset: 1,
          },
        }}
      >
        {/* Left: file glyph (or spinner during the lazy fetch). */}
        <Box
          sx={{
            flexShrink: 0,
            display: 'flex',
            alignItems: 'center',
            justifyContent: 'center',
            width: 20,
            height: 20,
          }}
        >
          {loading ? (
            <CircularProgress size={14} />
          ) : (
            <DescriptionIcon
              fontSize="small"
              sx={{ color: 'text.secondary', fontSize: 18 }}
            />
          )}
        </Box>

        {/* Middle: filename + size sub-line. minWidth:0 lets the text actually
            ellipsis inside a flex parent. */}
        <Stack sx={{ flex: 1, minWidth: 0 }} spacing={0}>
          <Typography
            variant="body2"
            sx={{
              fontWeight: 500,
              overflow: 'hidden',
              textOverflow: 'ellipsis',
              whiteSpace: 'nowrap',
              lineHeight: 1.3,
            }}
          >
            {fileName}
          </Typography>
          <Typography
            variant="caption"
            color="text.secondary"
            sx={{ lineHeight: 1.2 }}
          >
            {formatSize(sizeBytes)}
          </Typography>
        </Stack>

        {/* Right: external-link hint. Tiny + secondary so it reads as a quiet
            affordance, not an action button. */}
        <OpenInNewIcon
          fontSize="inherit"
          sx={{
            fontSize: 13,
            color: 'text.disabled',
            flexShrink: 0,
          }}
        />
      </Paper>
    </Tooltip>
  )
}

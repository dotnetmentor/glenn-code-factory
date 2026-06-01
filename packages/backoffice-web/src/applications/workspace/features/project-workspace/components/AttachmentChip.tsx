import {
  Box,
  CircularProgress,
  IconButton,
  LinearProgress,
  Paper,
  Stack,
  Tooltip,
  Typography,
} from '@mui/material'
import CloseIcon from '@mui/icons-material/Close'
import RefreshIcon from '@mui/icons-material/Refresh'
import CheckCircleIcon from '@mui/icons-material/CheckCircle'
import DescriptionIcon from '@mui/icons-material/Description'
import ErrorOutlineIcon from '@mui/icons-material/ErrorOutline'
import HourglassEmptyIcon from '@mui/icons-material/HourglassEmpty'
import type { AttachmentState } from '../hooks/useAttachmentUpload'

/**
 * Props for {@link AttachmentChip}.
 *
 * <p>This is a pure presentational component — it never makes a network call
 * and knows nothing about SignalR. The parent wires {@link useAttachmentUpload}
 * to the chip and passes through whatever the hook reports.</p>
 */
export interface AttachmentChipProps {
  state: AttachmentState
  fileName: string
  sizeBytes: number
  /** 0–100. Only consulted when {@code state === 'uploading'}. */
  progress?: number
  /** Failure-state human-readable error. */
  error?: string | null
  /** Always wired — every state has an X / Remove affordance. */
  onRemove: () => void
  /** Wired for {@code uploadFailed} and {@code stagingFailed} only. */
  onRetry?: () => void
}

/**
 * Per-state visual configuration. Centralising this keeps the JSX below
 * focused on layout and gives one obvious place to tweak the palette.
 */
type ChipVisual = {
  /** MUI palette token for the border. */
  borderColor: string
  /** Optional background tint (defaults to paper). */
  bgcolor?: string
  /** Short label shown next to (or under) the filename. */
  label: string
  /** Left-side icon. */
  icon: React.ReactNode
}

function visualFor(state: AttachmentState): ChipVisual {
  switch (state) {
    case 'queued':
      return {
        borderColor: 'divider',
        label: 'Queued',
        icon: <HourglassEmptyIcon fontSize="small" sx={{ color: 'text.secondary' }} />,
      }
    case 'uploading':
      return {
        borderColor: 'divider',
        label: 'Uploading',
        icon: <CircularProgress size={14} />,
      }
    case 'staging':
      return {
        borderColor: 'divider',
        label: 'Staging on runtime…',
        icon: <CircularProgress size={14} />,
      }
    case 'ready':
      return {
        borderColor: 'success.light',
        label: 'Ready',
        icon: <CheckCircleIcon fontSize="small" sx={{ color: 'success.main' }} />,
      }
    case 'uploadFailed':
      return {
        borderColor: 'error.main',
        // Subtle 8% red wash to distinguish from a neutral chip without
        // overwhelming the composer. MUI's palette ships `error.light` but
        // it's too saturated for a background fill — alpha on `error.main`
        // gives a calmer tint that reads as "warning" without shouting.
        bgcolor: 'rgba(211, 47, 47, 0.08)',
        label: 'Upload failed',
        icon: <ErrorOutlineIcon fontSize="small" sx={{ color: 'error.main' }} />,
      }
    case 'stagingFailed':
      return {
        borderColor: 'warning.main',
        bgcolor: 'rgba(237, 108, 2, 0.08)',
        label: "Couldn't deliver to agent runtime",
        icon: <ErrorOutlineIcon fontSize="small" sx={{ color: 'warning.main' }} />,
      }
    case 'rejected':
      return {
        borderColor: 'error.main',
        bgcolor: 'rgba(211, 47, 47, 0.08)',
        label: 'Rejected',
        icon: <ErrorOutlineIcon fontSize="small" sx={{ color: 'error.main' }} />,
      }
  }
}

/** Format bytes as B / KB / MB with one decimal. */
function formatSize(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`
  return `${(bytes / 1024 / 1024).toFixed(1)} MB`
}

/**
 * Per-attachment chip that renders all seven non-terminal UX states from the
 * {@code chat-file-attachments} spec (the 8th, "removed", isn't rendered —
 * the parent simply unmounts the chip).
 *
 * <p>Layout: a single rounded Paper with an icon on the left, a stack of
 * filename + sub-line (size, progress, or error) in the middle, and the
 * action buttons (Retry where applicable, then X) on the right. The
 * uploading state additionally renders a thin LinearProgress at the bottom
 * edge so the bar lines up across multiple chips dropped at once.</p>
 */
export function AttachmentChip(props: AttachmentChipProps) {
  const { state, fileName, sizeBytes, progress, error, onRemove, onRetry } =
    props

  const visual = visualFor(state)
  const showRetry =
    (state === 'uploadFailed' || state === 'stagingFailed') &&
    typeof onRetry === 'function'

  // Sub-line text under the filename — what the user actually reads to know
  // what's happening.
  let subline: React.ReactNode
  switch (state) {
    case 'uploading': {
      const pct =
        typeof progress === 'number' && progress >= 0 && progress <= 100
          ? Math.round(progress)
          : 0
      subline = (
        <Typography variant="caption" color="text.secondary">
          {formatSize(sizeBytes)} · Uploading {pct}%
        </Typography>
      )
      break
    }
    case 'staging':
      subline = (
        <Typography variant="caption" color="text.secondary">
          {formatSize(sizeBytes)} · Staging on runtime…
        </Typography>
      )
      break
    case 'ready':
      subline = (
        <Typography variant="caption" color="text.secondary">
          {formatSize(sizeBytes)} · Ready
        </Typography>
      )
      break
    case 'queued':
      subline = (
        <Typography variant="caption" color="text.secondary">
          {formatSize(sizeBytes)} · Queued
        </Typography>
      )
      break
    case 'uploadFailed':
    case 'stagingFailed':
    case 'rejected':
      subline = (
        <Typography variant="caption" color="error">
          {error ?? visual.label}
        </Typography>
      )
      break
  }

  // Compute the determinate value for the progress bar. We only render the
  // bar for 'uploading'; other states get no bar.
  const progressValue =
    state === 'uploading' &&
    typeof progress === 'number' &&
    progress >= 0 &&
    progress <= 100
      ? progress
      : 0

  return (
    <Paper
      variant="outlined"
      sx={{
        position: 'relative',
        display: 'flex',
        alignItems: 'center',
        gap: 1,
        px: 1,
        py: 0.75,
        borderRadius: 1.5,
        borderColor: visual.borderColor,
        bgcolor: visual.bgcolor ?? 'background.paper',
        // Cap the width so a wall of dropped files wraps gracefully in the
        // composer instead of all squashing onto one line.
        maxWidth: 320,
        minWidth: 200,
        overflow: 'hidden',
      }}
    >
      {/* Left: status icon (state-specific) with a small file glyph behind
          it on neutral states for type recognisability. */}
      <Box
        sx={{
          flexShrink: 0,
          display: 'flex',
          alignItems: 'center',
          justifyContent: 'center',
          width: 24,
          height: 24,
        }}
      >
        {visual.icon}
      </Box>

      {/* Middle: filename + sub-line. */}
      <Stack sx={{ flex: 1, minWidth: 0 }} spacing={0.25}>
        <Tooltip title={fileName} placement="top" enterDelay={400}>
          <Stack direction="row" spacing={0.5} alignItems="center" sx={{ minWidth: 0 }}>
            <DescriptionIcon
              fontSize="inherit"
              sx={{
                fontSize: 14,
                color: 'text.secondary',
                flexShrink: 0,
              }}
            />
            <Typography
              variant="body2"
              sx={{
                fontWeight: 500,
                overflow: 'hidden',
                textOverflow: 'ellipsis',
                whiteSpace: 'nowrap',
              }}
            >
              {fileName}
            </Typography>
          </Stack>
        </Tooltip>
        {subline}
      </Stack>

      {/* Right: actions. Retry is only present for the two retry-able
          failure states. Remove is always present. */}
      <Stack direction="row" spacing={0} sx={{ flexShrink: 0 }}>
        {showRetry && (
          <Tooltip title="Retry">
            <IconButton
              size="small"
              onClick={onRetry}
              aria-label={`Retry upload of ${fileName}`}
            >
              <RefreshIcon fontSize="small" />
            </IconButton>
          </Tooltip>
        )}
        <Tooltip title="Remove">
          <IconButton
            size="small"
            onClick={onRemove}
            aria-label={`Remove ${fileName}`}
          >
            <CloseIcon fontSize="small" />
          </IconButton>
        </Tooltip>
      </Stack>

      {/* Bottom-edge upload progress bar — only rendered while uploading so
          the chip doesn't carry visual weight in calm states. */}
      {state === 'uploading' && (
        <LinearProgress
          variant="determinate"
          value={progressValue}
          sx={{
            position: 'absolute',
            left: 0,
            right: 0,
            bottom: 0,
            height: 2,
            borderBottomLeftRadius: 1.5 * 8,
            borderBottomRightRadius: 1.5 * 8,
          }}
        />
      )}
    </Paper>
  )
}

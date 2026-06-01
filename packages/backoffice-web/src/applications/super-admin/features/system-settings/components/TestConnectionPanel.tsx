import {
  Accordion,
  AccordionDetails,
  AccordionSummary,
  Alert,
  Box,
  Button,
  CircularProgress,
  Paper,
  Stack,
  Typography,
} from '@mui/material'
import CheckCircleIcon from '@mui/icons-material/CheckCircle'
import CancelIcon from '@mui/icons-material/Cancel'
import ExpandMoreIcon from '@mui/icons-material/ExpandMore'
import type { ReactNode } from 'react'

export interface TestConnectionPanelProps {
  /** Heading shown in the header strip (e.g. "GitHub configuration"). */
  title: string
  /** Whether the test mutation is currently in flight. */
  isPending: boolean
  /** Whether the most recent test reported success. Null before the first call. */
  isValid: boolean | null
  /** Top-line message from the diagnostic response. */
  message: string | null
  /** Whether a result (or error) should be rendered below the button. */
  hasResult: boolean
  /** Renders the configuration-presence / category-specific detail sections. */
  details?: ReactNode
  /** Click handler — should fire the mutation. */
  onTest: () => void
  /** Optional human-readable error when the request itself failed. */
  requestError?: string | null
}

/**
 * Generic shell for the per-integration "Test connection" experience.
 *
 * - Header strip with a title on the left and the action button on the right.
 * - When the mutation has run at least once, an Alert summarises the verdict.
 * - Detail content is wrapped in a collapsed-by-default Accordion.
 *
 * The structure here is intentionally dumb: the wiring (which mutation to
 * fire, how to format detail rows) lives in the per-integration wrappers.
 */
export function TestConnectionPanel({
  title,
  isPending,
  isValid,
  message,
  hasResult,
  details,
  onTest,
  requestError,
}: TestConnectionPanelProps) {
  const showResult = hasResult && !isPending

  return (
    <Paper variant="outlined" sx={{ p: 3 }}>
      <Stack spacing={2}>
        <Box
          sx={{
            display: 'flex',
            alignItems: 'center',
            justifyContent: 'space-between',
            gap: 2,
            flexWrap: 'wrap',
          }}
        >
          <Typography variant="subtitle1" sx={{ fontWeight: 600 }}>
            {title}
          </Typography>
          <Button
            variant="outlined"
            size="small"
            onClick={onTest}
            disabled={isPending}
            startIcon={
              isPending ? (
                <CircularProgress size={14} color="inherit" />
              ) : undefined
            }
          >
            {isPending ? 'Testing…' : 'Test connection'}
          </Button>
        </Box>

        {requestError && !isPending && (
          <Alert severity="error">{requestError}</Alert>
        )}

        {showResult && isValid !== null && (
          <Alert severity={isValid ? 'success' : 'error'}>
            {message ?? (isValid ? 'Connection OK.' : 'Connection failed.')}
          </Alert>
        )}

        {showResult && details && (
          <Accordion disableGutters variant="outlined" sx={{ '&:before': { display: 'none' } }}>
            <AccordionSummary expandIcon={<ExpandMoreIcon />}>
              <Typography variant="body2">Show details</Typography>
            </AccordionSummary>
            <AccordionDetails>{details}</AccordionDetails>
          </Accordion>
        )}
      </Stack>
    </Paper>
  )
}

// -----------------------------------------------------------------------------
// Small presentational helpers used by the per-integration wrappers.
// -----------------------------------------------------------------------------

export interface FlagRowProps {
  label: string
  ok: boolean
}

/** A single boolean-flag row: green check or red X with the label. */
export function FlagRow({ label, ok }: FlagRowProps) {
  return (
    <Box sx={{ display: 'flex', alignItems: 'center', gap: 1 }}>
      {ok ? (
        <CheckCircleIcon fontSize="small" color="success" />
      ) : (
        <CancelIcon fontSize="small" color="error" />
      )}
      <Typography variant="body2">{label}</Typography>
    </Box>
  )
}

export interface FlagGroupProps {
  heading: string
  flags: ReadonlyArray<{ label: string; ok: boolean }>
}

/** A labelled cluster of flag rows, e.g. "App: AppId / PrivateKeyPem / AppSlug". */
export function FlagGroup({ heading, flags }: FlagGroupProps) {
  return (
    <Box>
      <Typography
        variant="caption"
        color="text.secondary"
        sx={{ fontWeight: 600, textTransform: 'uppercase', letterSpacing: 0.4 }}
      >
        {heading}
      </Typography>
      <Stack spacing={0.5} sx={{ mt: 0.5 }}>
        {flags.map((f) => (
          <FlagRow key={f.label} label={f.label} ok={f.ok} />
        ))}
      </Stack>
    </Box>
  )
}

export interface DetailSectionProps {
  heading: string
  ok: boolean
  /** Short pass/fail summary line, e.g. "OK (status 200)" or "Failed". */
  summary: string
  /** Optional error text shown in a monospace block when ok=false. */
  errorText?: string | null
  /** Optional extra content (e.g. an info table) shown when ok=true. */
  children?: ReactNode
}

/**
 * One vertical section in the details accordion (e.g. "JWT mint", "GitHub /app call").
 * Always shows a status line; renders an error code block on failure or
 * the children block on success.
 */
export function DetailSection({
  heading,
  ok,
  summary,
  errorText,
  children,
}: DetailSectionProps) {
  return (
    <Box>
      <Typography
        variant="caption"
        color="text.secondary"
        sx={{ fontWeight: 600, textTransform: 'uppercase', letterSpacing: 0.4 }}
      >
        {heading}
      </Typography>
      <Box sx={{ display: 'flex', alignItems: 'center', gap: 1, mt: 0.5 }}>
        {ok ? (
          <CheckCircleIcon fontSize="small" color="success" />
        ) : (
          <CancelIcon fontSize="small" color="error" />
        )}
        <Typography variant="body2">{summary}</Typography>
      </Box>
      {!ok && errorText && (
        <Box
          component="pre"
          sx={{
            mt: 1,
            p: 1.5,
            bgcolor: 'action.hover',
            borderRadius: 1,
            fontFamily: 'monospace',
            fontSize: 12,
            whiteSpace: 'pre-wrap',
            wordBreak: 'break-word',
            m: 0,
          }}
        >
          <code>{errorText}</code>
        </Box>
      )}
      {ok && children && <Box sx={{ mt: 1 }}>{children}</Box>}
    </Box>
  )
}

export interface KeyValueRowProps {
  label: string
  value: string | null | undefined
}

/** Two-column label/value row used for things like "App name: foo". */
export function KeyValueRow({ label, value }: KeyValueRowProps) {
  return (
    <Box
      sx={{
        display: 'grid',
        gridTemplateColumns: '160px 1fr',
        columnGap: 2,
        alignItems: 'baseline',
      }}
    >
      <Typography variant="caption" color="text.secondary">
        {label}
      </Typography>
      <Typography variant="body2" sx={{ fontFamily: 'monospace' }}>
        {value ?? '—'}
      </Typography>
    </Box>
  )
}

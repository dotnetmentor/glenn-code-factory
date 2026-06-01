import { useEffect, useMemo, useState } from 'react'
import {
  Alert,
  Box,
  Button,
  CircularProgress,
  Dialog,
  IconButton,
  Snackbar,
  Stack,
  Typography,
} from '@mui/material'
import CloseIcon from '@mui/icons-material/Close'
import SendIcon from '@mui/icons-material/Send'
import ErrorOutlineIcon from '@mui/icons-material/ErrorOutline'
import { useQueryClient } from '@tanstack/react-query'
import {
  getGetApiProjectsProjectIdRuntimeSpecQueryKey,
  usePutApiProjectsProjectIdRuntimeSpec,
  type ProblemDetails,
  type RuntimeSpecV3,
} from '@/api/queries-commands'
import {
  workspaceAccent,
  workspaceColors,
  workspaceFontFamily,
  workspaceText,
} from '@/applications/workspace/shared/designTokens'
import { validateSpec } from './specValidation'

export interface PasteSpecDialogProps {
  open: boolean
  onClose: () => void
  projectId: string
}

const PLACEHOLDER = `{
  "version": 3,
  "services": [
    {
      "kind": "node-vite",
      "name": "web",
      "values": { "project": "packages/web", "port": 5173 }
    }
  ]
}`

/**
 * Dialog for seeding a project's runtime spec by pasting raw JSON.
 *
 * <p>Surfaces in the Spec tab's empty state — no chat-driven proposal flow
 * required. Validates JSON parseability inline, then runs the parsed spec
 * through the same {@link validateSpec} the editor uses so the rules stay
 * consistent. On Apply the backend writes {@code project.Spec}, bumps
 * {@code SpecVersion}, and pushes a SignalR delta to the most-recent live
 * runtime (lazy propagation, same contract as Approve).</p>
 *
 * <p>Visually matches {@code SpecEditorDialog} — chrome-toned header strip
 * with uppercase label + title, monospace textarea inside a hairline card,
 * pill primary button in the footer.</p>
 */
export function PasteSpecDialog({
  open,
  onClose,
  projectId,
}: PasteSpecDialogProps) {
  const queryClient = useQueryClient()
  const mutation = usePutApiProjectsProjectIdRuntimeSpec()

  const [text, setText] = useState('')
  const [submitError, setSubmitError] = useState<string | null>(null)
  const [successOpen, setSuccessOpen] = useState(false)

  useEffect(() => {
    if (open) {
      setText('')
      setSubmitError(null)
    }
  }, [open])

  // Parse the textarea contents on every keystroke. Empty text means "nothing
  // to validate yet" — we don't surface a parser error until the user has
  // actually typed something, otherwise the dialog reads as broken on open.
  const parseState = useMemo<
    | { kind: 'empty' }
    | { kind: 'parse-error'; message: string }
    | { kind: 'parsed'; spec: RuntimeSpecV3 }
  >(() => {
    const trimmed = text.trim()
    if (trimmed.length === 0) return { kind: 'empty' }
    try {
      const parsed = JSON.parse(trimmed)
      if (typeof parsed !== 'object' || parsed === null || Array.isArray(parsed)) {
        return {
          kind: 'parse-error',
          message: 'Expected a JSON object at the top level.',
        }
      }
      return { kind: 'parsed', spec: parsed as RuntimeSpecV3 }
    } catch (err) {
      return {
        kind: 'parse-error',
        message:
          err instanceof Error ? err.message : 'Invalid JSON. Fix syntax to apply.',
      }
    }
  }, [text])

  const validation = useMemo(() => {
    if (parseState.kind !== 'parsed') return null
    return validateSpec(parseState.spec)
  }, [parseState])

  const isMeaningful =
    parseState.kind === 'parsed' && isMeaningfulSpec(parseState.spec)

  const canApply =
    parseState.kind === 'parsed' &&
    (validation?.isValid ?? false) &&
    isMeaningful &&
    !mutation.isPending

  const handleClose = () => {
    if (mutation.isPending) return
    onClose()
  }

  const handleApply = () => {
    if (parseState.kind !== 'parsed') return
    if (!validation?.isValid) return
    setSubmitError(null)
    mutation.mutate(
      {
        projectId,
        data: { spec: parseState.spec },
      },
      {
        onSuccess: () => {
          queryClient.invalidateQueries({
            queryKey: getGetApiProjectsProjectIdRuntimeSpecQueryKey(projectId),
          })
          setSuccessOpen(true)
          onClose()
        },
        onError: (err) => {
          setSubmitError(describeError(err))
        },
      },
    )
  }

  return (
    <>
      <Dialog
        open={open}
        onClose={handleClose}
        maxWidth="md"
        fullWidth
        PaperProps={{
          sx: {
            backgroundColor: 'instrument.canvas',
            color: workspaceText.primary,
          },
        }}
      >
        {/* Workspace-toned chrome strip — matches SpecEditorDialog. */}
        <Stack
          direction="row"
          alignItems="center"
          spacing={1.5}
          sx={{
            flexShrink: 0,
            height: 56,
            px: 3,
            backgroundColor: 'instrument.chrome',
            borderBottom: 1,
            borderColor: 'instrument.hairline',
          }}
        >
          <IconButton
            size="small"
            aria-label="Close paste spec dialog"
            onClick={handleClose}
            disabled={mutation.isPending}
            sx={{
              color: workspaceText.muted,
              transition: 'color 200ms ease',
              '&:hover': {
                color: workspaceAccent.ink,
                backgroundColor: 'transparent',
              },
            }}
          >
            <CloseIcon fontSize="small" />
          </IconButton>
          <Stack direction="row" spacing={1.5} alignItems="baseline">
            <Typography
              sx={{
                fontSize: '0.6875rem',
                fontWeight: 600,
                letterSpacing: '0.08em',
                textTransform: 'uppercase',
                color: workspaceText.muted,
              }}
            >
              Paste
            </Typography>
            <Typography
              component="h2"
              sx={{
                fontSize: '0.9375rem',
                fontWeight: 500,
                letterSpacing: '-0.005em',
                color: workspaceText.primary,
              }}
            >
              Runtime spec JSON
            </Typography>
          </Stack>
        </Stack>

        <Box
          sx={{
            display: 'flex',
            flexDirection: 'column',
            p: 3,
            gap: 2,
            backgroundColor: 'instrument.canvas',
          }}
        >
          <Typography
            sx={{
              fontSize: 13.5,
              color: workspaceText.muted,
              lineHeight: 1.55,
            }}
          >
            Paste a {`{ install, setup, services }`} object. Applying writes the
            spec directly to this project — if a runtime is live, it will
            receive the change immediately. Otherwise the next boot picks it
            up.
          </Typography>

          {parseState.kind === 'parse-error' && (
            <Alert
              variant="errorStrip"
              severity="error"
              icon={<ErrorOutlineIcon sx={{ fontSize: 14 }} />}
            >
              {parseState.message}
            </Alert>
          )}

          {parseState.kind === 'parsed' &&
            validation &&
            !validation.isValid && (
              <Alert
                variant="errorStrip"
                severity="error"
                icon={<ErrorOutlineIcon sx={{ fontSize: 14 }} />}
              >
                Fix {validation.errorMessages.length} validation issue
                {validation.errorMessages.length === 1 ? '' : 's'}:{' '}
                {validation.errorMessages.join(' · ')}
              </Alert>
            )}

          {parseState.kind === 'parsed' &&
            validation?.isValid &&
            !isMeaningful && (
              <Alert
                variant="errorStrip"
                severity="error"
                icon={<ErrorOutlineIcon sx={{ fontSize: 14 }} />}
              >
                Spec is empty — add at least an install command, setup
                command, or service before applying.
              </Alert>
            )}

          {submitError && (
            <Alert
              variant="errorStrip"
              severity="error"
              icon={<ErrorOutlineIcon sx={{ fontSize: 14 }} />}
            >
              {submitError}
            </Alert>
          )}

          <Box
            sx={{
              border: 1,
              borderColor: 'instrument.hairline',
              borderRadius: 1.5,
              overflow: 'hidden',
              backgroundColor: 'instrument.canvas',
            }}
          >
            <Box
              component="textarea"
              value={text}
              onChange={(e: React.ChangeEvent<HTMLTextAreaElement>) =>
                setText(e.target.value)
              }
              placeholder={PLACEHOLDER}
              spellCheck={false}
              autoCorrect="off"
              autoCapitalize="off"
              disabled={mutation.isPending}
              aria-label="Runtime spec JSON"
              rows={16}
              sx={{
                display: 'block',
                width: '100%',
                boxSizing: 'border-box',
                resize: 'vertical',
                minHeight: 360,
                m: 0,
                p: 2,
                border: 0,
                outline: 0,
                backgroundColor: workspaceColors.canvasBg,
                color: workspaceText.primary,
                fontFamily: workspaceFontFamily.mono,
                fontSize: 12.5,
                lineHeight: 1.55,
                whiteSpace: 'pre',
                overflowWrap: 'normal',
                overflowX: 'auto',
                '&::placeholder': {
                  color: workspaceText.faint,
                },
                '&:focus-visible': {
                  outline: 0,
                },
              }}
            />
          </Box>
        </Box>

        {/* Footer — Cancel + bronze Apply spec. */}
        <Stack
          direction="row"
          spacing={1.5}
          alignItems="center"
          justifyContent="flex-end"
          sx={{
            flexShrink: 0,
            px: 3,
            height: 60,
            backgroundColor: 'instrument.chrome',
            borderTop: 1,
            borderColor: 'instrument.hairline',
          }}
        >
          <Button
            variant="quietText"
            onClick={handleClose}
            disabled={mutation.isPending}
          >
            Cancel
          </Button>
          <Button
            variant="pill"
            color="primary"
            onClick={handleApply}
            disabled={!canApply}
            startIcon={
              mutation.isPending ? (
                <CircularProgress
                  size={14}
                  sx={{ color: 'instrument.canvas' }}
                />
              ) : (
                <SendIcon sx={{ fontSize: 16 }} />
              )
            }
          >
            {mutation.isPending ? 'Applying…' : 'Apply spec'}
          </Button>
        </Stack>
      </Dialog>

      <Snackbar
        open={successOpen}
        autoHideDuration={4000}
        onClose={() => setSuccessOpen(false)}
        anchorOrigin={{ vertical: 'bottom', horizontal: 'center' }}
      >
        <Alert
          severity="success"
          variant="filled"
          onClose={() => setSuccessOpen(false)}
        >
          Project spec applied.
        </Alert>
      </Snackbar>
    </>
  )
}

/**
 * Mirror of the {@code isMeaningfulSpec} helper in {@code SpecTab.tsx} —
 * an empty {@code {}} object is parseable but worthless, so we refuse to
 * apply a spec that has no install, no setup, and no services.
 */
function isMeaningfulSpec(spec: RuntimeSpecV3): boolean {
  const hasInstall = !!spec.install && spec.install.trim().length > 0
  const hasSetup = !!spec.setup && spec.setup.trim().length > 0
  const hasServices = !!spec.services && spec.services.length > 0
  return hasInstall || hasSetup || hasServices
}

/**
 * Pulls the backend's validation message out of an Orval-thrown error.
 * The PUT endpoint returns 400 ProblemDetails on validation issues; surface
 * the detail/title so the user sees exactly why the apply was rejected.
 */
function describeError(err: unknown): string {
  const maybe = err as
    | { response?: { data?: ProblemDetails & { error?: string } } }
    | undefined
  const data = maybe?.response?.data
  const detail =
    data?.detail ?? data?.error ?? data?.title ?? ''
  if (detail && detail.trim().length > 0) return detail
  if (err instanceof Error && err.message) return err.message
  return 'Failed to apply spec. Try again.'
}

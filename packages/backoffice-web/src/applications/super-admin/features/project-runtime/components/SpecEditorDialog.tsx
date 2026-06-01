import { useEffect, useMemo, useState } from 'react'
import {
  Alert,
  Box,
  Button,
  CircularProgress,
  Dialog,
  IconButton,
  Slide,
  Snackbar,
  Stack,
  Typography,
} from '@mui/material'
import CloseIcon from '@mui/icons-material/Close'
import SendIcon from '@mui/icons-material/Send'
import ErrorOutlineIcon from '@mui/icons-material/ErrorOutline'
import InfoOutlinedIcon from '@mui/icons-material/InfoOutlined'
import { TransitionProps } from '@mui/material/transitions'
import { forwardRef } from 'react'
import ReactDiffViewer, { DiffMethod } from 'react-diff-viewer-continued'
import { useQueryClient } from '@tanstack/react-query'
import {
  RuntimeProposalStatus,
  getGetApiProjectsProjectIdProposalsQueryKey,
  getGetApiProjectsProjectIdRuntimeSpecQueryKey,
  useGetApiProjectsProjectIdProposals,
  usePostApiProjectsProjectIdProposals,
  usePostApiProjectsProjectIdProposalsProposalIdEdit,
  type RuntimeSpecV3,
} from '@/api/queries-commands'
import {
  workspaceAccent,
  workspaceColors,
  workspaceFontFamily,
  workspaceText,
} from '@/applications/workspace/shared/designTokens'
import { SpecEditorForm } from './SpecEditorForm'
import { SpecEditorJson } from './SpecEditorJson'
import { canonicalizeSpec, validateSpec } from './specValidation'

const Transition = forwardRef(function Transition(
  props: TransitionProps & { children: React.ReactElement },
  ref: React.Ref<unknown>,
) {
  return <Slide direction="up" ref={ref} {...props} />
})

export interface SpecEditorDialogProps {
  open: boolean
  onClose: () => void
  projectId: string
  /** Currently-applied spec used as the initial draft + diff base. */
  currentSpec: RuntimeSpecV3
}

type EditorView = 'form' | 'json'

/**
 * Full-screen editor for the project's runtime spec. Hosts the Form and
 * JSON views over a single shared {@code draftSpec} state and gates
 * submission on the validation rules in {@code specValidation}.
 *
 * <p>Visually the dialog reads as a workspace surface — warm-paper canvas,
 * hairline-bottom chrome strip, bronze accents on hover/focus, SFMono for
 * code. The legacy MUI {@code AppBar} / {@code Toolbar} / blue contained
 * buttons that used to wrap it have been replaced with the workspace
 * vocabulary so this dialog feels like a continuation of the chat canvas
 * rather than a foreign Material surface dropped on top.</p>
 */
export function SpecEditorDialog({
  open,
  onClose,
  projectId,
  currentSpec,
}: SpecEditorDialogProps) {
  const queryClient = useQueryClient()
  const [view, setView] = useState<EditorView>('form')
  const [draftSpec, setDraftSpec] = useState<RuntimeSpecV3>(currentSpec)
  const [confirmOpen, setConfirmOpen] = useState(false)
  const [submitError, setSubmitError] = useState<string | null>(null)
  const [successOpen, setSuccessOpen] = useState(false)

  useEffect(() => {
    if (open) {
      setDraftSpec(currentSpec)
      setView('form')
      setSubmitError(null)
    }
  }, [open, currentSpec])

  const validation = useMemo(() => validateSpec(draftSpec), [draftSpec])
  const currentCanonical = useMemo(
    () => canonicalizeSpec(currentSpec),
    [currentSpec],
  )
  const draftCanonical = useMemo(
    () => canonicalizeSpec(draftSpec),
    [draftSpec],
  )
  const isUnchanged = currentCanonical === draftCanonical

  const proposalsQuery = useGetApiProjectsProjectIdProposals(
    projectId,
    { status: RuntimeProposalStatus.Pending, limit: 5 },
    { query: { enabled: open && !!projectId } },
  )

  const pendingProposalId = useMemo<string | undefined>(() => {
    const list = proposalsQuery.data ?? []
    const pending = list.filter(
      (p) => p.status === RuntimeProposalStatus.Pending,
    )
    if (pending.length === 0) return undefined
    return [...pending].sort(
      (a, b) =>
        new Date(b.createdAt).getTime() - new Date(a.createdAt).getTime(),
    )[0].id
  }, [proposalsQuery.data])

  const createProposal = usePostApiProjectsProjectIdProposals()
  const editProposal = usePostApiProjectsProjectIdProposalsProposalIdEdit()
  const isSubmitting = createProposal.isPending || editProposal.isPending

  const invalidate = () => {
    queryClient.invalidateQueries({
      queryKey: getGetApiProjectsProjectIdProposalsQueryKey(projectId),
    })
    queryClient.invalidateQueries({
      queryKey: getGetApiProjectsProjectIdRuntimeSpecQueryKey(projectId),
    })
  }

  const handleSubmit = () => {
    setSubmitError(null)
    const onError = (err: unknown) => {
      const msg =
        err instanceof Error
          ? err.message
          : 'Failed to submit proposal. Please try again.'
      setSubmitError(msg)
    }

    if (pendingProposalId) {
      editProposal.mutate(
        {
          projectId,
          proposalId: pendingProposalId,
          data: { editedSpec: draftSpec },
        },
        {
          onSuccess: () => {
            invalidate()
            setConfirmOpen(false)
            setSuccessOpen(true)
            onClose()
          },
          onError,
        },
      )
      return
    }

    createProposal.mutate(
      {
        projectId,
        data: { proposedSpec: draftSpec, reason: 'Edited via spec editor' },
      },
      {
        onSuccess: () => {
          invalidate()
          setConfirmOpen(false)
          setSuccessOpen(true)
          onClose()
        },
        onError,
      },
    )
  }

  const disableSubmit = !validation.isValid || isUnchanged

  return (
    <>
      <Dialog
        fullScreen
        open={open}
        onClose={onClose}
        TransitionComponent={Transition}
        PaperProps={{
          sx: {
            backgroundColor: 'instrument.canvas',
            color: workspaceText.primary,
          },
        }}
      >
        {/* Workspace-toned chrome strip — replaces the MUI AppBar/Toolbar. */}
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
            aria-label="Close spec editor"
            onClick={onClose}
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
              Edit
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
              Runtime spec
            </Typography>
          </Stack>

          <Box sx={{ flex: 1 }} />

          {/* View tabs — gentle pill switcher matching the debug panel. */}
          <Stack
            direction="row"
            role="tablist"
            aria-label="Editor view"
            sx={{
              bgcolor: 'instrument.chipBg',
              borderRadius: 999,
              p: 0.25,
              gap: 0.25,
            }}
          >
            {(['form', 'json'] as const).map((v) => {
              const active = view === v
              return (
                <Box
                  key={v}
                  component="button"
                  type="button"
                  role="tab"
                  aria-selected={active}
                  onClick={() => setView(v)}
                  sx={{
                    border: 0,
                    outline: 0,
                    cursor: 'pointer',
                    px: 1.5,
                    py: 0.375,
                    fontSize: '0.75rem',
                    fontWeight: active ? 600 : 500,
                    letterSpacing: '-0.005em',
                    borderRadius: 999,
                    bgcolor: active ? 'instrument.canvas' : 'transparent',
                    color: active ? workspaceAccent.ink : workspaceText.muted,
                    transition: 'background-color 160ms ease, color 160ms ease',
                    boxShadow: active ? '0 1px 2px rgba(0,0,0,0.05)' : 'none',
                    '&:hover': {
                      color: active ? workspaceAccent.ink : workspaceText.primary,
                      bgcolor: active ? 'instrument.canvas' : 'instrument.chipHoverBg',
                    },
                    '&:focus-visible': {
                      outline: `2px solid ${workspaceAccent.ink}`,
                      outlineOffset: 1,
                    },
                  }}
                >
                  {v === 'form' ? 'Form' : 'JSON'}
                </Box>
              )
            })}
          </Stack>
        </Stack>

        {/* Inline status strips — replaces the loud full-width MUI Alert. */}
        {!validation.isValid && (
          <Alert
            variant="errorStrip"
            severity="error"
            icon={<ErrorOutlineIcon sx={{ fontSize: 14 }} />}
          >
            Fix {validation.errorMessages.length} validation issue
            {validation.errorMessages.length === 1 ? '' : 's'} before submitting.
          </Alert>
        )}
        {isUnchanged && validation.isValid && (
          <Alert
            variant="infoStrip"
            severity="info"
            icon={<InfoOutlinedIcon sx={{ fontSize: 14 }} />}
          >
            No changes yet — edit the spec to enable submit.
          </Alert>
        )}

        <Box
          sx={{
            flex: 1,
            minHeight: 0,
            overflow: view === 'json' ? 'hidden' : 'auto',
            display: 'flex',
            flexDirection: 'column',
            backgroundColor: 'instrument.canvas',
          }}
        >
          {view === 'form' && (
            <SpecEditorForm
              spec={draftSpec}
              validation={validation}
              onChange={setDraftSpec}
            />
          )}
          {view === 'json' && (
            <SpecEditorJson spec={draftSpec} onChange={setDraftSpec} />
          )}
        </Box>

        {/* Footer — Cancel + bronze Propose changes. */}
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
          <Button variant="quietText" onClick={onClose}>
            Cancel
          </Button>
          <Button
            variant="pill"
            color="primary"
            onClick={() => setConfirmOpen(true)}
            disabled={disableSubmit || isSubmitting}
            startIcon={<SendIcon sx={{ fontSize: 16 }} />}          >
            Propose changes
          </Button>
        </Stack>
      </Dialog>

      {/* Confirmation dialog — also workspace-toned (no more MUI primary blue). */}
      <Dialog
        open={confirmOpen}
        onClose={isSubmitting ? undefined : () => setConfirmOpen(false)}
        maxWidth="lg"
        fullWidth
        PaperProps={{
          sx: { boxShadow: '0 8px 32px rgba(0,0,0,0.12)' },
        }}
      >
        <Stack
          direction="row"
          alignItems="center"
          sx={{
            px: 3,
            height: 56,
            backgroundColor: 'instrument.chrome',
            borderBottom: 1,
            borderColor: 'instrument.hairline',
          }}
        >
          <Typography
            sx={{
              fontSize: '0.9375rem',
              fontWeight: 500,
              letterSpacing: '-0.005em',
              color: workspaceText.primary,
            }}
          >
            {pendingProposalId
              ? 'Update pending proposal?'
              : 'Submit new proposal?'}
          </Typography>
        </Stack>

        <Box sx={{ p: 3, maxHeight: '70vh', overflowY: 'auto' }}>
          <Stack spacing={2}>
            <Typography
              sx={{
                fontSize: 13.5,
                color: workspaceText.muted,
                letterSpacing: '-0.005em',
                lineHeight: 1.55,
              }}
            >
              Review the diff between the currently-applied spec and your
              proposed changes. The proposal still requires approval before
              the daemon applies it.
            </Typography>
            {submitError && (
              <Alert
                variant="errorStrip"
                severity="error"
                icon={<ErrorOutlineIcon sx={{ fontSize: 14 }} />}
                sx={{
                  borderRadius: 1,
                  border: 1,
                  borderColor: 'instrument.error',
                }}
              >
                {submitError}
              </Alert>
            )}
            <Box
              sx={{
                border: 1,
                borderColor: 'instrument.hairline',
                borderRadius: 1,
                overflow: 'hidden',
                fontSize: 12.5,
                backgroundColor: 'instrument.canvas',
                '& pre, & td.diff-cell, & div': {
                  fontFamily: workspaceFontFamily.mono,
                },
              }}
            >
              <ReactDiffViewer
                oldValue={currentCanonical}
                newValue={draftCanonical}
                splitView
                compareMethod={DiffMethod.LINES}
                leftTitle="Currently applied"
                rightTitle="Proposed"
                useDarkTheme={false}
                styles={MUTED_DIFF_STYLES}
              />
            </Box>
          </Stack>
        </Box>

        <Stack
          direction="row"
          spacing={1.5}
          justifyContent="flex-end"
          sx={{
            px: 3,
            py: 1.5,
            backgroundColor: 'instrument.chrome',
            borderTop: 1,
            borderColor: 'instrument.hairline',
          }}
        >
          <Button
            variant="quietText"
            onClick={() => setConfirmOpen(false)}
            disabled={isSubmitting}          >
            Cancel
          </Button>
          <Button
            variant="pill"
            color="primary"
            onClick={handleSubmit}
            disabled={isSubmitting}
            startIcon={
              isSubmitting ? (
                <CircularProgress size={14} sx={{ color: 'instrument.canvas' }} />
              ) : (
                <SendIcon sx={{ fontSize: 16 }} />
              )
            }          >
            {isSubmitting ? 'Submitting…' : 'Confirm submit'}
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
          {pendingProposalId
            ? 'Pending proposal updated.'
            : 'Proposal submitted for review.'}
        </Alert>
      </Snackbar>
    </>
  )
}

/**
 * Muted palette overrides for {@code react-diff-viewer-continued}. Replaces
 * the library's loud red/green wash with very pale bronze-red (removed)
 * and pale sage-green (added) tints so the diff sits inside a workspace
 * surface without the page screaming "ALERT".
 */
const MUTED_DIFF_STYLES = {
  variables: {
    light: {
      diffViewerBackground: workspaceColors.canvasBg,
      diffViewerColor: workspaceText.primary,
      addedBackground: 'rgba(127, 178, 87, 0.08)',
      addedColor: workspaceText.primary,
      removedBackground: 'rgba(178, 84, 56, 0.06)',
      removedColor: workspaceText.primary,
      wordAddedBackground: 'rgba(127, 178, 87, 0.22)',
      wordRemovedBackground: 'rgba(178, 84, 56, 0.18)',
      addedGutterBackground: 'rgba(127, 178, 87, 0.12)',
      removedGutterBackground: 'rgba(178, 84, 56, 0.08)',
      gutterBackground: workspaceColors.chipBg,
      gutterBackgroundDark: workspaceColors.chromeBg,
      highlightBackground: workspaceColors.chipHoverBg,
      highlightGutterBackground: workspaceColors.chipHoverBg,
      codeFoldGutterBackground: workspaceColors.chromeBg,
      codeFoldBackground: workspaceColors.chromeBg,
      emptyLineBackground: 'transparent',
      gutterColor: workspaceText.faint,
      addedGutterColor: workspaceText.muted,
      removedGutterColor: workspaceText.muted,
      codeFoldContentColor: workspaceText.muted,
      diffViewerTitleBackground: workspaceColors.chromeBg,
      diffViewerTitleColor: workspaceText.muted,
      diffViewerTitleBorderColor: workspaceColors.hairline,
    },
  },
  contentText: {
    fontFamily: workspaceFontFamily.mono,
  },
} as const

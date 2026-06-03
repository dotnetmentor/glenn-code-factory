import { useEffect, useMemo, useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { useQueryClient } from '@tanstack/react-query'
import {
  Box,
  Button,
  Dialog,
  DialogActions,
  DialogContent,
  DialogTitle,
  FormControl,
  FormControlLabel,
  MenuItem,
  Radio,
  RadioGroup,
  Select,
  Stack,
  TextField,
  Typography,
} from '@mui/material'
import {
  getGetApiProjectsProjectIdBranchesQueryKey,
  useGetApiWorkspacesWorkspaceIdSpecs,
  usePostApiProjectsProjectIdBranchesBranchIdCopy,
  type CopyBranchResponse,
  type ProblemDetails,
} from '../../../../../api/queries-commands'
import { useWorkspace } from '../../../../shared/contexts/WorkspaceContext'

import {
  chromeTokens,
  semanticTokens,
  surfaceTokens,
  workspaceAccent,
  workspaceFontFamily,
} from '../../../shared/designTokens'

const tokens = {
  canvas: surfaceTokens.canvasBg,
  surface: surfaceTokens.chromeBg,
  primary: surfaceTokens.textPrimary,
  muted: surfaceTokens.textMuted,
  hairline: surfaceTokens.hairline,
  accent: chromeTokens.accent,
  danger: semanticTokens.danger,
  rowHover: chromeTokens.rowHover,
  warmPanelBg: workspaceAccent.soft,
} as const

// ── Auto-suggested name helper ──────────────────────────────────────────────

/**
 * Suggest a copy name for the given source branch. Tries
 * {@code <sourceName>-copy} first, then {@code -copy-2}, {@code -copy-3}, …
 * until one is not in {@code existingNames}.
 *
 * <p>Comparison is case-sensitive — the backend treats branch names
 * case-sensitively, so the UI matches.</p>
 */
export function suggestCopyName(sourceName: string, existingNames: string[]): string {
  const set = new Set(existingNames)
  const base = `${sourceName}-copy`
  if (!set.has(base)) return base
  let n = 2
  // Bound the loop in case of a pathological input — 1000 collisions is
  // already absurd and we'd rather fall through than spin forever.
  while (n < 1000) {
    const candidate = `${base}-${n}`
    if (!set.has(candidate)) return candidate
    n += 1
  }
  return `${base}-${Date.now()}`
}

// ── Error mapping ──────────────────────────────────────────────────────────

interface AxiosLikeError {
  response?: {
    status?: number
    // Older endpoints can still emit the legacy { error: "..." } shape; we read
    // .detail / .title first (ProblemDetails) and fall back to .error so a
    // mid-migration mismatch doesn't blow away the real reason.
    data?: ProblemDetails & { error?: string }
  }
  message?: string
}

interface MappedError {
  /** 409 — source branch not yet pushed to GitHub. Top banner. */
  pushBlocker: string | null
  /** 422 — name conflict / validation. Inline beneath the input. */
  inlineError: string | null
  /** Everything else — inline, generic. */
  genericError: string | null
}

/**
 * cloudflare-tunnel-preview Phase 3: backend now returns 409 for two
 * distinct reasons on this endpoint —
 *   - "Source branch not pushed" → ProblemDetails (has .detail / .title)
 *   - `pool_empty` → bare body { error: "pool_empty" }
 * We discriminate on the literal error code so the existing push-blocker
 * copy stays correct, and pool exhaustion gets its own admin-actionable
 * message.
 */
const POOL_EMPTY_MESSAGE =
  'No preview subdomain available. Ask a system administrator to provision more in /super-admin/subdomains.'

function mapMutationError(err: unknown): MappedError {
  const e = err as AxiosLikeError | null | undefined
  const status = e?.response?.status
  const errorCode = e?.response?.data?.error ?? null
  const detail =
    e?.response?.data?.detail ??
    e?.response?.data?.title ??
    e?.response?.data?.error ??
    null
  if (status === 409 && errorCode === 'pool_empty') {
    return {
      pushBlocker: POOL_EMPTY_MESSAGE,
      inlineError: null,
      genericError: null,
    }
  }
  if (status === 409) {
    return {
      pushBlocker: 'Push the source branch to GitHub first, then try again.',
      inlineError: null,
      genericError: null,
    }
  }
  if (status === 422) {
    return {
      pushBlocker: null,
      inlineError: detail ?? 'That name is already taken.',
      genericError: null,
    }
  }
  return {
    pushBlocker: null,
    inlineError: null,
    genericError: detail ?? "Couldn't finish copying. Nothing was changed.",
  }
}

// ── Dialog component ───────────────────────────────────────────────────────

export interface CopyBranchDialogProps {
  open: boolean
  onClose: () => void
  /** Active workspace slug — used to build the navigation URL to the new branch. */
  slug: string
  projectId: string
  sourceBranchId: string
  sourceBranchName: string
  /** All branch names currently in the project — used for auto-suffix collision. */
  existingBranchNames: string[]
}

/**
 * Which "services" starter the user wants for the new runtime. The three modes
 * map onto the backend's two flags:
 *   - 'source'  → forceBlankSpec=false, catalogSpecId=null (fork source's spec)
 *   - 'catalog' → forceBlankSpec=false, catalogSpecId=<picked>
 *   - 'blank'   → forceBlankSpec=true,  catalogSpecId=null
 */
type ServicesMode = 'source' | 'catalog' | 'blank'

// ── Small visual primitives ───────────────────────────────────────────────

/** Small uppercase eyebrow label — matches the workspace's section headers. */
function Eyebrow({ children }: { children: React.ReactNode }) {
  return (
    <Typography
      component="span"
      sx={{
        fontSize: '0.6875rem',
        fontWeight: 600,
        letterSpacing: '0.08em',
        textTransform: 'uppercase',
        color: tokens.muted,
        lineHeight: 1.4,
      }}
    >
      {children}
    </Typography>
  )
}

/**
 * One radio row in the Services picker. Label + helper subtitle, styled to
 * sit in the dialog's warm palette and align the helper text with the label
 * (not the radio dot) so the column reads cleanly.
 */
function ServicesOption({
  value,
  label,
  helper,
  selected,
  disabled,
}: {
  value: string
  label: string
  helper: string
  selected: boolean
  disabled?: boolean
}) {
  return (
    <FormControlLabel
      value={value}
      disabled={disabled}
      control={
        <Radio
          size="small"
          sx={{
            color: tokens.muted,
            '&.Mui-checked': { color: tokens.accent },
            // Pull the dot up slightly so it sits flush with the first line
            // of the label rather than centring against the two-line block.
            alignSelf: 'flex-start',
            mt: 0.125,
            p: 0.5,
          }}
        />
      }
      sx={{
        alignItems: 'flex-start',
        mx: 0,
        mr: 0,
        py: 0.25,
        '& .MuiFormControlLabel-label': {
          flex: 1,
          // Tighten the gap between radio and label vs. MUI's default.
          pl: 0.25,
        },
      }}
      label={
        <Stack spacing={0.125} sx={{ minWidth: 0 }}>
          <Typography
            sx={{
              fontSize: '0.8125rem',
              fontWeight: selected ? 600 : 500,
              color: tokens.primary,
              lineHeight: 1.35,
            }}
          >
            {label}
          </Typography>
          <Typography
            sx={{
              fontSize: '0.6875rem',
              color: tokens.muted,
              lineHeight: 1.45,
            }}
          >
            {helper}
          </Typography>
        </Stack>
      }
    />
  )
}

/** Warm-tinted advisory panel used for the 409 push-blocker + generic errors. */
function WarmPanel({ children }: { children: React.ReactNode }) {
  return (
    <Box
      sx={{
        bgcolor: tokens.warmPanelBg,
        border: `1px solid ${tokens.hairline}`,
        borderRadius: 1.5,
        px: 1.5,
        py: 1.25,
      }}
    >
      <Typography
        sx={{
          fontSize: '0.8125rem',
          color: tokens.primary,
          fontWeight: 400,
          lineHeight: 1.45,
        }}
      >
        {children}
      </Typography>
    </Box>
  )
}

/**
 * Copy-branch dialog. A single form: pre-filled with {@code suggestCopyName(...)},
 * validates, and fires the
 * {@code usePostApiProjectsProjectIdBranchesBranchIdCopy} mutation. On success
 * we invalidate the branch list and immediately navigate to (and select) the
 * new branch — the destination branch page renders the runtime's provisioning
 * state, so we deliberately do NOT block navigation on the new runtime reaching
 * {@code Online} (which can take minutes and previously left the user stranded
 * on the source branch when the {@code Online} push was missed).
 */
export function CopyBranchDialog({
  open,
  onClose,
  slug,
  projectId,
  sourceBranchId,
  sourceBranchName,
  existingBranchNames,
}: CopyBranchDialogProps) {
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const { currentWorkspace } = useWorkspace()
  const workspaceId = currentWorkspace?.id ?? ''

  const suggested = useMemo(
    () => suggestCopyName(sourceBranchName, existingBranchNames),
    [sourceBranchName, existingBranchNames],
  )

  const [name, setName] = useState<string>(suggested)
  const [inlineError, setInlineError] = useState<string | null>(null)
  const [pushBlocker, setPushBlocker] = useState<string | null>(null)
  const [genericError, setGenericError] = useState<string | null>(null)
  const [servicesMode, setServicesMode] = useState<ServicesMode>('source')
  const [selectedCatalogSpecId, setSelectedCatalogSpecId] = useState<string>('')

  // Reset when the dialog opens for a new source branch.
  useEffect(() => {
    if (!open) return
    setName(suggested)
    setInlineError(null)
    setPushBlocker(null)
    setGenericError(null)
    setServicesMode('source')
    setSelectedCatalogSpecId('')
  }, [open, suggested, sourceBranchId])

  // Catalog specs for the workspace. We fetch unconditionally while the dialog
  // is open so toggling "Pick from catalog…" is instant — the list is small
  // (workspace-scoped) and the response is cached by TanStack Query so the
  // cost amortises across opens. We disable the query entirely when we don't
  // have a workspace id yet (the context can briefly resolve to null on first
  // load).
  const catalogSpecsQuery = useGetApiWorkspacesWorkspaceIdSpecs(workspaceId, {
    query: { enabled: open && !!workspaceId },
  })
  const catalogSpecs = catalogSpecsQuery.data ?? []
  const catalogEmpty =
    !catalogSpecsQuery.isLoading && catalogSpecs.length === 0

  const mutation = usePostApiProjectsProjectIdBranchesBranchIdCopy()

  const trimmedName = name.trim()

  // Submit is gated on the catalog pick when the user has chosen
  // "Pick from catalog…". We don't surface this as an inline error because the
  // disabled button + helper copy on the dropdown is enough signal.
  const catalogPickIncomplete =
    servicesMode === 'catalog' && !selectedCatalogSpecId

  const handleSubmit = (e?: React.FormEvent) => {
    if (e) e.preventDefault()
    if (!trimmedName) {
      setInlineError('Please enter a branch name.')
      return
    }
    if (catalogPickIncomplete) {
      // Guard against an Enter-key submit while the user hasn't picked yet.
      // No banner — the disabled CTA + dropdown helper carry the message.
      return
    }
    setInlineError(null)
    setPushBlocker(null)
    setGenericError(null)

    // Translate the radio mode into the two backend flags. Keep this branch
    // explicit (rather than clever) so the mapping is obvious at a glance.
    const forceBlankSpec = servicesMode === 'blank'
    const catalogSpecId =
      servicesMode === 'catalog' ? selectedCatalogSpecId : null

    mutation.mutate(
      {
        projectId,
        branchId: sourceBranchId,
        data: {
          name: trimmedName,
          catalogSpecId,
          forceBlankSpec,
        },
      },
      {
        onSuccess: (response: CopyBranchResponse) => {
          // Surface the new branch in the sidebar, then jump straight to it and
          // select it. The destination branch page owns the runtime
          // provisioning UI from here.
          queryClient.invalidateQueries({
            queryKey: getGetApiProjectsProjectIdBranchesQueryKey(projectId),
          })
          navigate(
            `/w/${slug}/projects/${projectId}/branches/${response.newBranchId}`,
          )
          onClose()
        },
        onError: (err) => {
          const mapped = mapMutationError(err)
          setPushBlocker(mapped.pushBlocker)
          setInlineError(mapped.inlineError)
          setGenericError(mapped.genericError)
        },
      },
    )
  }

  const handleClose = () => {
    if (mutation.isPending) return // No-op while the backend has work in flight.
    onClose()
  }

  // ── Title rendering ─────────────────────────────────────────────────────
  // We render the title manually so we can inline the source branch name
  // in monospace — matching the rest of the workspace's technical-name cadence.
  const titleNode = (
    <Stack direction="row" alignItems="baseline" spacing={0.75} sx={{ minWidth: 0 }}>
      <Box component="span" sx={{ flexShrink: 0 }}>
        Copy
      </Box>
      <Box
        component="span"
        sx={{
          fontFamily: workspaceFontFamily.mono,
          fontSize: '0.8125rem',
          fontWeight: 500,
          color: tokens.primary,
          overflow: 'hidden',
          textOverflow: 'ellipsis',
          whiteSpace: 'nowrap',
          minWidth: 0,
        }}
      >
        {sourceBranchName}
      </Box>
      <Box component="span" sx={{ flexShrink: 0, color: tokens.muted, fontWeight: 500 }}>
        to a new branch
      </Box>
    </Stack>
  )

  return (
    <Dialog
      open={open}
      onClose={handleClose}
      maxWidth={false}
      slotProps={{
        paper: {
          sx: {
            width: '100%',
            maxWidth: 480,
            bgcolor: tokens.canvas,
            border: `1px solid ${tokens.hairline}`,
            borderRadius: '12px',
            boxShadow: '0 20px 50px rgba(0,0,0,0.12)',
            backgroundImage: 'none',
          },
        },
      }}
    >
      <form onSubmit={handleSubmit}>
        <DialogTitle
          sx={{
            fontSize: '0.875rem',
            fontWeight: 600,
            color: tokens.primary,
            px: 3,
            pt: 2.5,
            pb: 1.5,
            borderBottom: 'none',
            lineHeight: 1.4,
          }}
        >
          {titleNode}
        </DialogTitle>

        <DialogContent sx={{ px: 3, pb: 2.5, pt: 0 }}>
          <Stack spacing={1.75}>
            {pushBlocker && <WarmPanel>{pushBlocker}</WarmPanel>}

            {/* Source eyebrow + branch name in monospace */}
            <Stack direction="row" alignItems="baseline" spacing={1} sx={{ minWidth: 0 }}>
              <Eyebrow>Source</Eyebrow>
              <Box
                component="span"
                sx={{
                  fontFamily: workspaceFontFamily.mono,
                  fontSize: '0.75rem',
                  color: tokens.primary,
                  overflow: 'hidden',
                  textOverflow: 'ellipsis',
                  whiteSpace: 'nowrap',
                  minWidth: 0,
                }}
              >
                {sourceBranchName}
              </Box>
            </Stack>

            {/* New-name field with eyebrow label */}
            <Stack spacing={0.75}>
              <Eyebrow>New branch name</Eyebrow>
              <TextField
                required
                autoFocus
                fullWidth
                size="small"
                value={name}
                onChange={(e) => {
                  setName(e.target.value)
                  setInlineError(null)
                  setGenericError(null)
                }}
                error={!!inlineError}
                disabled={mutation.isPending}
                inputProps={{ spellCheck: 'false', autoComplete: 'off' }}
                variant="outlined"
                sx={{
                  '& .MuiOutlinedInput-root': {
                    bgcolor: tokens.surface,
                    borderRadius: 1.5,
                    fontFamily: workspaceFontFamily.mono,
                    fontSize: '0.8125rem',
                    color: tokens.primary,
                    transition: 'border-color 120ms ease, background-color 120ms ease',
                    '&.Mui-error fieldset': {
                      borderColor: tokens.danger,
                    },
                    '&.Mui-disabled': {
                      bgcolor: tokens.surface,
                      opacity: 0.7,
                    },
                  },
                  '& .MuiOutlinedInput-input': {
                    px: 1.5,
                    py: 1,
                  },
                  '& .MuiOutlinedInput-input::placeholder': {
                    color: tokens.muted,
                    opacity: 1,
                  },
                }}
              />
              {inlineError ? (
                <Typography
                  sx={{
                    fontSize: '0.75rem',
                    color: tokens.danger,
                    lineHeight: 1.4,
                  }}
                >
                  {inlineError}
                </Typography>
              ) : (
                <Typography
                  sx={{
                    fontSize: '0.6875rem',
                    color: tokens.muted,
                    lineHeight: 1.45,
                  }}
                >
                  We suggested a non-conflicting name; you can change it.
                </Typography>
              )}
            </Stack>

            {/* Services picker — three options driving the backend's
                forceBlankSpec / catalogSpecId pair. Default "source" carries
                the source branch's current spec into the new runtime. */}
            <Stack spacing={0.75}>
              <Eyebrow>Services</Eyebrow>
              <RadioGroup
                value={servicesMode}
                onChange={(_, value) => {
                  const next = value as ServicesMode
                  setServicesMode(next)
                  if (next !== 'catalog') {
                    // Clear any stale pick so toggling back doesn't auto-
                    // submit with a now-irrelevant selection.
                    setSelectedCatalogSpecId('')
                  }
                }}
                sx={{ gap: 0.25 }}
              >
                <ServicesOption
                  value="source"
                  label="Same as source branch"
                  helper="Fork the source branch's current spec into the new runtime."
                  selected={servicesMode === 'source'}
                  disabled={mutation.isPending}
                />
                <ServicesOption
                  value="catalog"
                  label="Pick from catalog…"
                  helper="Stamp the new runtime with one of your workspace's saved specs."
                  selected={servicesMode === 'catalog'}
                  disabled={mutation.isPending}
                />
                {servicesMode === 'catalog' && (
                  <Box sx={{ pl: 3.75, pt: 0.5, pb: 0.25 }}>
                    <FormControl
                      fullWidth
                      size="small"
                      disabled={
                        mutation.isPending ||
                        catalogSpecsQuery.isLoading ||
                        catalogEmpty
                      }
                    >
                      <Select
                        value={selectedCatalogSpecId}
                        displayEmpty
                        onChange={(e) =>
                          setSelectedCatalogSpecId(e.target.value as string)
                        }
                        renderValue={(value) => {
                          if (!value) {
                            return (
                              <Box
                                component="span"
                                sx={{
                                  color: tokens.muted,
                                  fontSize: '0.8125rem',
                                }}
                              >
                                {catalogSpecsQuery.isLoading
                                  ? 'Loading…'
                                  : catalogEmpty
                                    ? 'No catalog specs available'
                                    : 'Select a spec…'}
                              </Box>
                            )
                          }
                          const picked = catalogSpecs.find(
                            (s) => s.id === value,
                          )
                          return picked?.name ?? value
                        }}
                        sx={{
                          bgcolor: tokens.surface,
                          borderRadius: 1.5,
                          fontSize: '0.8125rem',
                          color: tokens.primary,
                          '& .MuiSelect-select': {
                            py: 1,
                            px: 1.5,
                          },
                        }}
                      >
                        {catalogSpecs.map((spec) => (
                          <MenuItem
                            key={spec.id}
                            value={spec.id}
                            sx={{ fontSize: '0.8125rem' }}
                          >
                            <Stack spacing={0.125}>
                              <Box component="span" sx={{ fontWeight: 500 }}>
                                {spec.name}
                              </Box>
                              {spec.description && (
                                <Box
                                  component="span"
                                  sx={{
                                    fontSize: '0.6875rem',
                                    color: tokens.muted,
                                    lineHeight: 1.3,
                                  }}
                                >
                                  {spec.description}
                                </Box>
                              )}
                            </Stack>
                          </MenuItem>
                        ))}
                      </Select>
                    </FormControl>
                    {catalogEmpty && (
                      <Typography
                        sx={{
                          mt: 0.5,
                          fontSize: '0.6875rem',
                          color: tokens.muted,
                          lineHeight: 1.45,
                        }}
                      >
                        No specs in this workspace's catalog yet. Save one
                        from a runtime drawer first.
                      </Typography>
                    )}
                  </Box>
                )}
                <ServicesOption
                  value="blank"
                  label="Blank — no services"
                  helper="Start with an empty spec. The agent will propose services as needed."
                  selected={servicesMode === 'blank'}
                  disabled={mutation.isPending}
                />
              </RadioGroup>
            </Stack>

            {genericError && <WarmPanel>{genericError}</WarmPanel>}
          </Stack>
        </DialogContent>

        <DialogActions sx={{ px: 3, pb: 2.5, pt: 1, gap: 1 }}>
          <Button
            type="button"
            onClick={handleClose}
            disabled={mutation.isPending}
            variant="text"
            disableElevation
            sx={{
              color: tokens.muted,
              textTransform: 'none',
              fontWeight: 500,
              fontSize: '0.8125rem',
              borderRadius: 2,
              px: 1.5,
              py: 0.625,
              '&:hover': {
                bgcolor: tokens.rowHover,
                color: tokens.primary,
              },
            }}
          >
            Cancel
          </Button>
          <Button
            type="submit"
            variant="pill" color="primary" disabled={
              mutation.isPending || !trimmedName || catalogPickIncomplete
            }

            sx={{
              bgcolor: tokens.primary,
              color: tokens.canvas,
              textTransform: 'none',
              fontWeight: 500,
              fontSize: '0.8125rem',
              borderRadius: 2,
              px: 1.75,
              py: 0.625,
              boxShadow: 'none',
              '&:hover': {
                bgcolor: tokens.primary,
                boxShadow: 'none',
              },
              '&.Mui-disabled': {
                bgcolor: tokens.surface,
                color: tokens.muted,
              },
            }}
          >
            {mutation.isPending ? 'Copying…' : 'Copy branch'}
          </Button>
        </DialogActions>
      </form>
    </Dialog>
  )
}

// Re-export the helper as the named export the spec calls for, so consumers
// (and tests) can import it without reaching into the dialog file's defaults.
export default CopyBranchDialog

import { useMemo, useState, type MouseEvent } from 'react'
import {
  Autocomplete,
  Box,
  ListItemIcon,
  ListItemText,
  Menu,
  MenuItem,
  Popover,
  TextField,
  Tooltip,
} from '@mui/material'
import CheckIcon from '@mui/icons-material/Check'
import KeyboardArrowDownIcon from '@mui/icons-material/KeyboardArrowDown'
import {
  useGetApiClaudeModelsActive,
  useGetApiCursorModelsActive,
  useGetApiProjectsProjectId,
  type ClaudeModelDto,
} from '../../../../../api/queries-commands'
import {
  useAgentOverride,
  type AgentBackend,
  type ReasoningEffort,
} from '../hooks/useAgentModelOverride'

import {
  workspaceAccent,
  workspaceFontFamily,
  workspaceText,
} from '../../../shared/designTokens'

const COLOR_MUTED = workspaceText.disabled
const COLOR_PRIMARY = workspaceText.primary
const COLOR_ACCENT = workspaceAccent.ink

const DEFAULT_OPTION_ID = '__use-project-default__'

/**
 * Reasoning dropdown options. The stored value is the SDK {@code effort}; the
 * label is the user-facing rung. {@code xhigh} is the power-user rung between
 * High and Max — present so a model whose {@code defaultEffort} is {@code xhigh}
 * renders a sensible label, but it sits inline in the same menu.
 */
const REASONING_OPTIONS: ReadonlyArray<{
  effort: ReasoningEffort
  label: string
}> = [
  { effort: 'low', label: 'Low' },
  { effort: 'medium', label: 'Medium' },
  { effort: 'high', label: 'High' },
  { effort: 'xhigh', label: 'X-High' },
  { effort: 'max', label: 'Max' },
]

interface ComposerModelPickerInlineProps {
  projectId: string
  conversationId: string | null
}

type PickerOption = {
  id: string
  displayName: string
  slug: string
  aliases: string[]
  isDefaultSentinel: boolean
}

/**
 * Ambient per-conversation agent picker backed by an MUI Autocomplete.
 *
 * <p>Backend defaults to Cursor, in which case this renders byte-for-byte what
 * it always did: a single muted model-picker button sourcing the Cursor model
 * catalog. The Claude backend adds two unobtrusive sibling controls — a backend
 * toggle and (only for reasoning-capable Claude models) a reasoning dropdown —
 * reusing the exact same trigger-button + popover styling.</p>
 *
 * <p>All selection is persisted per-conversation via {@link useAgentOverride}.</p>
 */
export function ComposerModelPickerInline({
  projectId,
  conversationId,
}: ComposerModelPickerInlineProps) {
  const projectQuery = useGetApiProjectsProjectId(projectId, {
    query: { enabled: !!projectId },
  })

  const cursorModelsQuery = useGetApiCursorModelsActive({
    query: { staleTime: 30_000, enabled: !!projectId },
  })

  const { value: override, patch, clear } = useAgentOverride(conversationId)
  const backend = override.backend

  // Lazily fetch the Claude catalog only once the conversation is on (or being
  // switched to) the Claude backend — keeps the Cursor default path from
  // issuing an extra request it never needs.
  const claudeModelsQuery = useGetApiClaudeModelsActive({
    query: { staleTime: 30_000, enabled: !!projectId && backend === 'claude' },
  })

  const cursorModels: PickerOption[] = useMemo(
    () =>
      (cursorModelsQuery.data ?? []).map((m) => ({
        id: m.id,
        displayName: m.displayName,
        slug: m.slug,
        aliases: m.aliases ?? [],
        isDefaultSentinel: false,
      })),
    [cursorModelsQuery.data],
  )

  const claudeModels: PickerOption[] = useMemo(
    () =>
      (claudeModelsQuery.data ?? []).map((m) => ({
        id: m.id,
        displayName: m.displayName,
        slug: m.slug,
        aliases: [],
        isDefaultSentinel: false,
      })),
    [claudeModelsQuery.data],
  )

  // Quick lookup for reasoning capability + default effort by Claude model id.
  const claudeModelById = useMemo(() => {
    const map = new Map<string, ClaudeModelDto>()
    for (const m of claudeModelsQuery.data ?? []) map.set(m.id, m)
    return map
  }, [claudeModelsQuery.data])

  const project = projectQuery.data ?? null
  const projectDefaultModelId =
    backend === 'claude'
      ? (project?.claudeModelId ?? null)
      : (project?.modelId ?? null)
  const projectDefaultModelSlug =
    backend === 'claude'
      ? (project?.claudeModelSlug ?? null)
      : (project?.modelSlug ?? null)

  // The active model id for this backend: explicit override wins, else the
  // project/catalog default. (For Claude, a null project default falls through
  // to "system default" labelling.)
  const overrideModelId = override.model
  const effectiveModelId = overrideModelId ?? projectDefaultModelId ?? null

  const activeModels = backend === 'claude' ? claudeModels : cursorModels

  // For the Claude system default, surface the catalog row flagged
  // isSystemDefault so the label isn't a bare "Default".
  const claudeSystemDefault = useMemo(
    () =>
      backend === 'claude'
        ? ((claudeModelsQuery.data ?? []).find((m) => m.isSystemDefault) ?? null)
        : null,
    [backend, claudeModelsQuery.data],
  )

  const effectiveModel = useMemo(
    () => activeModels.find((m) => m.id === effectiveModelId) ?? null,
    [activeModels, effectiveModelId],
  )
  const effectiveLabel = (() => {
    if (effectiveModel) return effectiveModel.displayName
    if (projectDefaultModelSlug) return projectDefaultModelSlug
    if (backend === 'claude' && claudeSystemDefault)
      return claudeSystemDefault.displayName
    return 'Default'
  })()

  const optionsWithDefault: PickerOption[] = useMemo(
    () => [
      {
        id: DEFAULT_OPTION_ID,
        displayName:
          backend === 'claude' ? 'Use system default' : 'Use project default',
        slug:
          projectDefaultModelSlug ??
          (backend === 'claude'
            ? (claudeSystemDefault?.slug ?? 'System default')
            : 'System default'),
        aliases: [],
        isDefaultSentinel: true,
      },
      ...activeModels,
    ],
    [activeModels, backend, projectDefaultModelSlug, claudeSystemDefault],
  )

  const selectedRowId = overrideModelId ?? DEFAULT_OPTION_ID

  // ── reasoning state ────────────────────────────────────────────────────────
  // The reasoning control only exists for a reasoning-capable Claude model.
  const selectedClaudeModel =
    backend === 'claude' && effectiveModelId
      ? (claudeModelById.get(effectiveModelId) ?? null)
      : null
  const showReasoning =
    backend === 'claude' && (selectedClaudeModel?.supportsReasoning ?? false)
  const defaultEffort = coerceEffort(selectedClaudeModel?.defaultEffort)
  const effectiveEffort: ReasoningEffort | null =
    override.reasoningEffort ?? defaultEffort
  const reasoningLabel =
    REASONING_OPTIONS.find((o) => o.effort === effectiveEffort)?.label ??
    'Default'

  // ── popover / menu anchors ──────────────────────────────────────────────────
  const [anchor, setAnchor] = useState<HTMLElement | null>(null)
  const [searchInput, setSearchInput] = useState('')
  const [backendAnchor, setBackendAnchor] = useState<HTMLElement | null>(null)
  const [reasoningAnchor, setReasoningAnchor] = useState<HTMLElement | null>(
    null,
  )

  const openPopover = (e: MouseEvent<HTMLElement>) => {
    setSearchInput('')
    setAnchor(e.currentTarget)
  }
  const closePopover = () => {
    setAnchor(null)
    setSearchInput('')
  }
  const onSelect = (next: PickerOption | null) => {
    closePopover()
    if (!conversationId) return
    if (!next) return
    // Selecting the sentinel resets only the model (keeping the backend); other
    // options write the chosen id under the current backend.
    if (next.isDefaultSentinel) {
      if (backend === 'cursor' && override.reasoningEffort === null) {
        // Pure cursor + no effort → fully default, clear the record entirely so
        // legacy/clean state is preserved exactly as before.
        clear()
      } else {
        patch({ model: null })
      }
      return
    }
    patch({ model: next.id })
  }

  const onSelectBackend = (next: AgentBackend) => {
    setBackendAnchor(null)
    if (!conversationId) return
    if (next === backend) return
    // Switching backend resets the model selection (the id spaces don't overlap)
    // and drops any reasoning effort so the new model's default applies.
    patch({ backend: next, model: null, reasoningEffort: null })
  }

  const onSelectReasoning = (next: ReasoningEffort | null) => {
    setReasoningAnchor(null)
    if (!conversationId) return
    patch({ reasoningEffort: next })
  }

  if (!conversationId) return null
  // The Cursor default path stays identical to before: if the Cursor catalog
  // hasn't loaded (or is empty) render nothing — same guard as the original.
  if (backend === 'cursor' && cursorModels.length === 0) return null

  const popoverOpen = Boolean(anchor)
  const triggerButtonSx = {
    display: 'inline-flex',
    alignItems: 'center',
    gap: 0.25,
    border: 0,
    background: 'none',
    padding: '2px 4px',
    fontSize: '0.6875rem',
    fontWeight: 500,
    color: COLOR_MUTED,
    cursor: 'pointer',
    letterSpacing: '-0.005em',
    fontFamily: 'inherit',
    lineHeight: 1.3,
    borderRadius: 0.5,
    transition: 'color 120ms ease',
    '&:hover': {
      color: COLOR_PRIMARY,
    },
    '&:focus-visible': {
      outline: `1px solid ${COLOR_ACCENT}`,
      outlineOffset: 1,
    },
  } as const

  return (
    <Box
      sx={{
        display: 'inline-flex',
        alignItems: 'center',
        gap: 0.25,
        minWidth: 0,
      }}
    >
      {/* Backend selector — reuses the muted trigger-button + Menu pattern. */}
      <Tooltip title="Switch agent backend" enterDelay={400}>
        <Box
          component="button"
          type="button"
          onClick={(e: MouseEvent<HTMLElement>) =>
            setBackendAnchor(e.currentTarget)
          }
          aria-haspopup="menu"
          aria-expanded={Boolean(backendAnchor)}
          aria-label="Switch agent backend"
          sx={triggerButtonSx}
        >
          <Box component="span">{backend === 'claude' ? 'Claude' : 'Cursor'}</Box>
          <KeyboardArrowDownIcon sx={{ fontSize: 12, opacity: 0.55 }} />
        </Box>
      </Tooltip>
      <Menu
        open={Boolean(backendAnchor)}
        anchorEl={backendAnchor}
        onClose={() => setBackendAnchor(null)}
        anchorOrigin={{ vertical: 'top', horizontal: 'left' }}
        transformOrigin={{ vertical: 'bottom', horizontal: 'left' }}
        slotProps={{
          paper: {
            sx: {
              mb: 0.5,
              minWidth: 160,
              border: 1,
              borderColor: 'instrument.hairline',
              boxShadow: '0 2px 12px rgba(0,0,0,0.08)',
            },
          },
        }}
      >
        {(['cursor', 'claude'] as const).map((b) => (
          <MenuItem
            key={b}
            dense
            selected={b === backend}
            onClick={() => onSelectBackend(b)}
            sx={{ fontSize: '0.8125rem' }}
          >
            <ListItemIcon sx={{ color: COLOR_ACCENT, minWidth: 28 }}>
              {b === backend ? (
                <CheckIcon fontSize="small" />
              ) : (
                <Box sx={{ width: 20, height: 20 }} />
              )}
            </ListItemIcon>
            {b === 'claude' ? 'Claude' : 'Cursor'}
          </MenuItem>
        ))}
      </Menu>

      <Box component="span" sx={{ color: COLOR_MUTED, fontSize: '0.6875rem', opacity: 0.5 }}>
        /
      </Box>

      {/* Model picker — identical trigger + searchable Popover as before. */}
      <Tooltip title="Switch agent model" enterDelay={400}>
        <Box
          component="button"
          type="button"
          onClick={openPopover}
          aria-haspopup="listbox"
          aria-expanded={popoverOpen}
          aria-label="Switch agent model"
          sx={triggerButtonSx}
        >
          <Box
            component="span"
            sx={{
              whiteSpace: 'nowrap',
              overflow: 'hidden',
              textOverflow: 'ellipsis',
              maxWidth: 200,
            }}
            title={effectiveLabel}
          >
            {effectiveLabel}
          </Box>
          <KeyboardArrowDownIcon sx={{ fontSize: 12, opacity: 0.55 }} />
        </Box>
      </Tooltip>
      <Popover
        open={popoverOpen}
        anchorEl={anchor}
        onClose={closePopover}
        anchorOrigin={{ vertical: 'top', horizontal: 'right' }}
        transformOrigin={{ vertical: 'bottom', horizontal: 'right' }}
        slotProps={{
          paper: {
            sx: {
              mb: 0.5,
              width: 360,
              boxShadow: '0 2px 12px rgba(0,0,0,0.08)',
              border: 1,
              borderColor: 'instrument.hairline',
              overflow: 'hidden',
            },
          },
        }}
      >
        <Autocomplete<PickerOption, false, false, false>
          open
          disablePortal
          value={null}
          onChange={(_, val) => onSelect(val)}
          options={optionsWithDefault}
          inputValue={searchInput}
          onInputChange={(_, val) => setSearchInput(val)}
          getOptionLabel={(option) => option.displayName}
          isOptionEqualToValue={(o, v) => o.id === v.id}
          autoHighlight={false}
          filterOptions={(opts, state) => {
            const q = state.inputValue.trim().toLowerCase()
            if (!q) return opts
            return opts.filter((o) => {
              if (o.isDefaultSentinel) {
                return (
                  o.displayName.toLowerCase().includes(q) ||
                  o.slug.toLowerCase().includes(q)
                )
              }
              if (o.displayName.toLowerCase().includes(q)) return true
              if (o.slug.toLowerCase().includes(q)) return true
              return o.aliases.some((a) => a.toLowerCase().includes(q))
            })
          }}
          renderInput={(params) => (
            <TextField
              {...params}
              autoFocus
              placeholder="Search models"
              size="small"
              inputProps={{
                ...params.inputProps,
                'aria-label': 'Search agent models',
              }}
              sx={{
                p: 1,
                '& .MuiOutlinedInput-root': {
                  fontSize: '0.8125rem',
                },
              }}
            />
          )}
          renderOption={(props, option) => {
            const { key: _key, ...rest } =
              props as typeof props & { key?: string }
            const isSentinel = option.isDefaultSentinel
            const selected = option.id === selectedRowId
            return (
              <Box
                component="li"
                {...rest}
                key={option.id}
                sx={{
                  display: 'flex',
                  alignItems: 'flex-start',
                  gap: 0,
                  px: 1,
                  py: 0.75,
                  fontSize: '0.8125rem',
                }}
              >
                <ListItemIcon sx={{ color: COLOR_ACCENT, minWidth: 28, mt: 0.25 }}>
                  {selected ? (
                    <CheckIcon fontSize="small" />
                  ) : (
                    <Box sx={{ width: 20, height: 20 }} />
                  )}
                </ListItemIcon>
                <ListItemText
                  primaryTypographyProps={{
                    fontSize: '0.8125rem',
                    fontWeight: selected ? 600 : 500,
                    color: COLOR_PRIMARY,
                    lineHeight: 1.35,
                  }}
                  secondaryTypographyProps={{
                    component: 'div',
                    fontSize: '0.6875rem',
                    color: COLOR_MUTED,
                  }}
                  primary={option.displayName}
                  secondary={
                    <>
                      <Box
                        component="span"
                        sx={{
                          display: 'block',
                          fontFamily: isSentinel ? 'inherit' : workspaceFontFamily.mono,
                          color: COLOR_MUTED,
                          fontSize: '0.6875rem',
                          lineHeight: 1.4,
                        }}
                      >
                        {option.slug}
                      </Box>
                      {!isSentinel && option.aliases.length > 0 && (
                        <Box
                          component="span"
                          sx={{
                            display: 'block',
                            fontFamily: workspaceFontFamily.mono,
                            color: 'rgba(0, 0, 0, 0.32)',
                            fontSize: '0.625rem',
                            lineHeight: 1.4,
                            mt: 0.125,
                          }}
                        >
                          aliases: {option.aliases.join(', ')}
                        </Box>
                      )}
                    </>
                  }
                />
              </Box>
            )
          }}
          slotProps={{
            paper: {
              sx: {
                boxShadow: 'none',
                border: 0,
                borderRadius: 0,
                m: 0,
              },
            },
            listbox: {
              sx: {
                maxHeight: 360,
                py: 0,
              },
            },
            popper: {
              sx: {
                width: '100% !important',
                position: 'static !important' as 'static',
                transform: 'none !important' as 'none',
              },
            },
          }}
          forcePopupIcon={false}
        />
      </Popover>

      {/* Reasoning dropdown — Claude + reasoning-capable model only. */}
      {showReasoning && (
        <>
          <Box
            component="span"
            sx={{ color: COLOR_MUTED, fontSize: '0.6875rem', opacity: 0.5 }}
          >
            /
          </Box>
          <Tooltip title="Reasoning effort" enterDelay={400}>
            <Box
              component="button"
              type="button"
              onClick={(e: MouseEvent<HTMLElement>) =>
                setReasoningAnchor(e.currentTarget)
              }
              aria-haspopup="menu"
              aria-expanded={Boolean(reasoningAnchor)}
              aria-label="Reasoning effort"
              sx={triggerButtonSx}
            >
              <Box component="span">{reasoningLabel}</Box>
              <KeyboardArrowDownIcon sx={{ fontSize: 12, opacity: 0.55 }} />
            </Box>
          </Tooltip>
          <Menu
            open={Boolean(reasoningAnchor)}
            anchorEl={reasoningAnchor}
            onClose={() => setReasoningAnchor(null)}
            anchorOrigin={{ vertical: 'top', horizontal: 'left' }}
            transformOrigin={{ vertical: 'bottom', horizontal: 'left' }}
            slotProps={{
              paper: {
                sx: {
                  mb: 0.5,
                  minWidth: 160,
                  border: 1,
                  borderColor: 'instrument.hairline',
                  boxShadow: '0 2px 12px rgba(0,0,0,0.08)',
                },
              },
            }}
          >
            <MenuItem
              dense
              selected={override.reasoningEffort === null}
              onClick={() => onSelectReasoning(null)}
              sx={{ fontSize: '0.8125rem' }}
            >
              <ListItemIcon sx={{ color: COLOR_ACCENT, minWidth: 28 }}>
                {override.reasoningEffort === null ? (
                  <CheckIcon fontSize="small" />
                ) : (
                  <Box sx={{ width: 20, height: 20 }} />
                )}
              </ListItemIcon>
              {`Default${defaultEffort ? ` (${defaultEffort})` : ''}`}
            </MenuItem>
            {REASONING_OPTIONS.map((o) => {
              const selected = override.reasoningEffort === o.effort
              return (
                <MenuItem
                  key={o.effort}
                  dense
                  selected={selected}
                  onClick={() => onSelectReasoning(o.effort)}
                  sx={{ fontSize: '0.8125rem' }}
                >
                  <ListItemIcon sx={{ color: COLOR_ACCENT, minWidth: 28 }}>
                    {selected ? (
                      <CheckIcon fontSize="small" />
                    ) : (
                      <Box sx={{ width: 20, height: 20 }} />
                    )}
                  </ListItemIcon>
                  {o.label}
                </MenuItem>
              )
            })}
          </Menu>
        </>
      )}
    </Box>
  )
}

function coerceEffort(raw: unknown): ReasoningEffort | null {
  return raw === 'low' ||
    raw === 'medium' ||
    raw === 'high' ||
    raw === 'xhigh' ||
    raw === 'max'
    ? raw
    : null
}

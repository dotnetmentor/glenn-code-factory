import { useMemo, useState, type MouseEvent } from 'react'
import {
  Autocomplete,
  Box,
  ListItemIcon,
  ListItemText,
  Popover,
  TextField,
  Tooltip,
} from '@mui/material'
import CheckIcon from '@mui/icons-material/Check'
import KeyboardArrowDownIcon from '@mui/icons-material/KeyboardArrowDown'
import {
  useGetApiCursorModelsActive,
  useGetApiProjectsProjectId,
} from '../../../../../api/queries-commands'
import { useAgentModelOverride } from '../hooks/useAgentModelOverride'

import {
  workspaceAccent,
  workspaceFontFamily,
  workspaceText,
} from '../../../shared/designTokens'

const COLOR_MUTED = workspaceText.disabled
const COLOR_PRIMARY = workspaceText.primary
const COLOR_ACCENT = workspaceAccent.ink

const DEFAULT_OPTION_ID = '__use-project-default__'

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
 * Ambient per-conversation Cursor model picker backed by an MUI Autocomplete.
 * Selection is persisted per-conversation via {@link useAgentModelOverride}.
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

  const activeModels: PickerOption[] = useMemo(
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

  const { value: overrideId, setOverride } = useAgentModelOverride(conversationId)

  const project = projectQuery.data ?? null
  const projectDefaultModelId = project?.modelId ?? null
  const projectDefaultModelSlug = project?.modelSlug ?? null

  const effectiveModelId = overrideId ?? projectDefaultModelId ?? null
  const effectiveModel = useMemo(
    () => activeModels.find((m) => m.id === effectiveModelId) ?? null,
    [activeModels, effectiveModelId],
  )
  const effectiveLabel = (() => {
    if (effectiveModel) return effectiveModel.displayName
    if (overrideId === null && projectDefaultModelSlug) return projectDefaultModelSlug
    return 'Default'
  })()

  const optionsWithDefault: PickerOption[] = useMemo(
    () => [
      {
        id: DEFAULT_OPTION_ID,
        displayName: 'Use project default',
        slug: projectDefaultModelSlug ?? 'System default',
        aliases: [],
        isDefaultSentinel: true,
      },
      ...activeModels,
    ],
    [activeModels, projectDefaultModelSlug],
  )

  const selectedRowId = overrideId ?? DEFAULT_OPTION_ID

  const [anchor, setAnchor] = useState<HTMLElement | null>(null)
  const [searchInput, setSearchInput] = useState('')
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
    setOverride(next.isDefaultSentinel ? null : next.id)
  }

  if (!conversationId) return null
  if (activeModels.length === 0) return null

  const popoverOpen = Boolean(anchor)

  return (
    <>
      <Tooltip title="Switch agent model" enterDelay={400}>
        <Box
          component="button"
          type="button"
          onClick={openPopover}
          aria-haspopup="listbox"
          aria-expanded={popoverOpen}
          aria-label="Switch agent model"
          sx={{
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
          }}
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
    </>
  )
}

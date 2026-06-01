import { useMemo, useState } from 'react'
import {
  Alert,
  Box,
  Dialog,
  IconButton,
  Skeleton,
  Stack,
  Typography,
} from '@mui/material'
import CloseIcon from '@mui/icons-material/Close'
import AddCircleOutlineIcon from '@mui/icons-material/AddCircleOutline'
import StorageIcon from '@mui/icons-material/Storage'
import MemoryIcon from '@mui/icons-material/Memory'
import QueueIcon from '@mui/icons-material/Queue'
import MailOutlineIcon from '@mui/icons-material/MailOutline'
import FolderIcon from '@mui/icons-material/Folder'
import ExtensionIcon from '@mui/icons-material/Extension'
import ErrorOutlineIcon from '@mui/icons-material/ErrorOutline'
import {
  useGetApiAdminRuntimePresets,
  type ServicePresetDto,
  type ServiceInstance,
} from '@/api/queries-commands'
import {
  workspaceAccent,
  workspaceColors,
  workspaceRuntime,
  workspaceText,
} from '@/applications/workspace/shared/designTokens'

export interface PresetPickerDialogProps {
  open: boolean
  onClose: () => void
  /** Called when the user picks a preset (or "Custom service"). */
  onSelect: (serviceInstance: ServiceInstance) => void
}

const CATEGORIES = [
  'all',
  'backend',
  'frontend',
  'database',
  'worker',
  'other',
] as const
type CategoryFilter = (typeof CATEGORIES)[number]

const CATEGORY_LABELS: Record<CategoryFilter, string> = {
  all: 'All',
  backend: 'Backend',
  frontend: 'Frontend',
  database: 'Database',
  worker: 'Worker',
  other: 'Other',
}

/**
 * Modal preset picker used by the runtime spec editor's "Add service"
 * button. Loads the DB-backed preset library from the backend via Orval
 * (V3: {@code ServicePresetDto}) and renders a filterable gallery.
 * Selecting a preset yields a {@link ServiceInstance} pre-populated with
 * the preset's parameter defaults.
 *
 * <p>Visually the picker mirrors the workspace surface — hairline-bordered
 * paper cards, bronze on hover, chips that match the conversation-strip
 * chips in {@code ChatChrome}. The "Custom service" card sits visually
 * distinct via a dashed hairline at the top of the grid.</p>
 */
export function PresetPickerDialog({
  open,
  onClose,
  onSelect,
}: PresetPickerDialogProps) {
  const [category, setCategory] = useState<CategoryFilter>('all')

  const presetsQuery = useGetApiAdminRuntimePresets({
    query: { enabled: open },
  })

  const presets = useMemo<ServicePresetDto[]>(() => {
    const list = presetsQuery.data ?? []
    // Stable order: built-ins first (by slug), then user presets (by slug).
    return [...list].sort((a, b) => {
      if (a.isBuiltIn !== b.isBuiltIn) return a.isBuiltIn ? -1 : 1
      return a.slug.localeCompare(b.slug)
    })
  }, [presetsQuery.data])

  const filteredPresets = useMemo(() => {
    if (category === 'all') return presets
    return presets.filter((p) => normalizeCategory(p.category) === category)
  }, [presets, category])

  const handleSelectPreset = (preset: ServicePresetDto) => {
    onSelect(buildInstanceFromPreset(preset))
  }

  const handleSelectCustom = () => {
    // "Custom" is now the bash-raw escape hatch: a blank-kind instance
    // the user fills in by switching to the JSON editor.
    onSelect({ kind: '', name: '', values: {} })
  }

  return (
    <Dialog
      open={open}
      onClose={onClose}
      maxWidth="md"
      fullWidth
      scroll="paper"
      PaperProps={{
        sx: {
          backgroundColor: workspaceColors.canvasBg,
          border: `1px solid ${workspaceColors.hairline}`,
          boxShadow: '0 8px 32px rgba(0,0,0,0.12)',
        },
      }}
    >
      <Stack
        direction="row"
        alignItems="center"
        sx={{
          flexShrink: 0,
          px: 3,
          height: 56,
          backgroundColor: workspaceColors.chromeBg,
          borderBottom: `1px solid ${workspaceColors.hairline}`,
        }}
      >
        <Stack direction="row" spacing={1.5} alignItems="baseline" sx={{ flex: 1, minWidth: 0 }}>
          <Typography
            sx={{
              fontSize: '0.6875rem',
              fontWeight: 600,
              letterSpacing: '0.08em',
              textTransform: 'uppercase',
              color: workspaceText.muted,
            }}
          >
            Add
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
            Service preset
          </Typography>
        </Stack>
        <IconButton
          size="small"
          aria-label="Close preset picker"
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
      </Stack>

      <Box sx={{ p: 3, maxHeight: '70vh', overflowY: 'auto' }}>
        <Typography
          sx={{
            fontSize: 13.5,
            color: workspaceText.muted,
            letterSpacing: '-0.005em',
            lineHeight: 1.55,
            mb: 2,
          }}
        >
          Pick a preset to drop a ready-to-run service into your spec, or start
          from a blank entry.
        </Typography>

        <Stack
          direction="row"
          spacing={0.75}
          sx={{ mb: 2.5, flexWrap: 'wrap', rowGap: 0.75 }}
        >
          {CATEGORIES.map((c) => {
            const active = category === c
            return (
              <Box
                key={c}
                component="button"
                type="button"
                onClick={() => setCategory(c)}
                sx={{
                  border: 0,
                  outline: 0,
                  cursor: 'pointer',
                  px: 1.25,
                  py: 0.5,
                  fontSize: '0.75rem',
                  fontWeight: active ? 600 : 500,
                  letterSpacing: '-0.005em',
                  borderRadius: 999,
                  bgcolor: active ? workspaceColors.canvasBg : workspaceColors.chipBg,
                  color: active ? workspaceAccent.ink : workspaceText.muted,
                  transition: 'background-color 160ms ease, color 160ms ease',
                  outlineColor: active ? workspaceAccent.ink : 'transparent',
                  outlineStyle: 'solid',
                  outlineWidth: active ? '1px' : 0,
                  '&:hover': {
                    color: active ? workspaceAccent.ink : workspaceText.primary,
                    bgcolor: active ? workspaceColors.canvasBg : workspaceColors.chipHoverBg,
                  },
                  '&:focus-visible': {
                    outline: `2px solid ${workspaceAccent.ink}`,
                    outlineOffset: 1,
                  },
                }}
              >
                {CATEGORY_LABELS[c]}
              </Box>
            )
          })}
        </Stack>

        <Box
          sx={{
            display: 'grid',
            gap: 2,
            gridTemplateColumns: {
              xs: '1fr',
              sm: 'repeat(2, 1fr)',
              md: 'repeat(3, 1fr)',
            },
          }}
        >
          <CustomServiceCard onClick={handleSelectCustom} />

          {presetsQuery.isLoading &&
            Array.from({ length: 6 }).map((_, i) => (
              <Skeleton
                key={`skel-${i}`}
                variant="rounded"
                height={148}
                animation="wave"
                sx={{
                  bgcolor: workspaceColors.chipBg,
                  borderRadius: 1.5,
                }}
              />
            ))}

          {presetsQuery.isError && (
            <Box sx={{ gridColumn: '1 / -1' }}>
              <Alert
                variant="errorStrip"
                severity="error"
                icon={<ErrorOutlineIcon sx={{ fontSize: 14 }} />}
              >
                Failed to load presets. Please try again.
              </Alert>
            </Box>
          )}

          {!presetsQuery.isLoading &&
            !presetsQuery.isError &&
            filteredPresets.length === 0 && (
              <Box sx={{ gridColumn: '1 / -1' }}>
                <Typography
                  sx={{
                    fontSize: 13.5,
                    color: workspaceText.muted,
                    fontStyle: 'italic',
                  }}
                >
                  No presets found
                  {category !== 'all'
                    ? ` in "${CATEGORY_LABELS[category]}"`
                    : ''}
                  .
                </Typography>
              </Box>
            )}

          {filteredPresets.map((preset) => (
            <PresetCard
              key={preset.id}
              preset={preset}
              onClick={() => handleSelectPreset(preset)}
            />
          ))}
        </Box>
      </Box>
    </Dialog>
  )
}

function CustomServiceCard({ onClick }: { onClick: () => void }) {
  return (
    <Box
      component="button"
      type="button"
      onClick={onClick}
      data-testid="preset-picker-custom"
      sx={{
        textAlign: 'left',
        cursor: 'pointer',
        border: `1px dashed ${workspaceColors.hairline}`,
        borderRadius: 1.5,
        bgcolor: workspaceColors.chipBg,
        p: 2,
        display: 'flex',
        flexDirection: 'column',
        gap: 1.25,
        transition: 'border-color 160ms ease, background-color 160ms ease',
        '&:hover': {
          borderColor: workspaceAccent.ink,
          bgcolor: workspaceColors.chipHoverBg,
        },
        '&:focus-visible': {
          outline: `2px solid ${workspaceAccent.ink}`,
          outlineOffset: 1,
        },
      }}
    >
      <Stack direction="row" spacing={1.25} alignItems="center">
        <Box
          sx={{
            width: 32,
            height: 32,
            borderRadius: 1,
            display: 'flex',
            alignItems: 'center',
            justifyContent: 'center',
            color: workspaceAccent.ink,
            bgcolor: workspaceAccent.surface,
          }}
        >
          <AddCircleOutlineIcon sx={{ fontSize: 18 }} />
        </Box>
        <Stack sx={{ minWidth: 0, flex: 1 }} spacing={0.25}>
          <Typography
            sx={{
              fontSize: 14,
              fontWeight: 500,
              color: workspaceText.primary,
              letterSpacing: '-0.005em',
            }}
          >
            Blank service
          </Typography>
          <Typography
            sx={{
              fontSize: '0.6875rem',
              fontWeight: 600,
              letterSpacing: '0.08em',
              textTransform: 'uppercase',
              color: workspaceText.faint,
            }}
          >
            Custom kind
          </Typography>
        </Stack>
      </Stack>
      <Typography
        sx={{
          fontSize: 12.5,
          color: workspaceText.muted,
          lineHeight: 1.5,
          display: '-webkit-box',
          WebkitLineClamp: 2,
          WebkitBoxOrient: 'vertical',
          overflow: 'hidden',
        }}
      >
        Start from scratch. Fill in a preset slug as the kind, then values
        the preset expects.
      </Typography>
    </Box>
  )
}

interface PresetCardProps {
  preset: ServicePresetDto
  onClick: () => void
}

function PresetCard({ preset, onClick }: PresetCardProps) {
  const categoryKey = normalizeCategory(preset.category)
  const label = CATEGORY_LABELS[categoryKey] ?? preset.category

  return (
    <Box
      component="button"
      type="button"
      onClick={onClick}
      data-testid={`preset-card-${preset.slug}`}
      sx={{
        textAlign: 'left',
        cursor: 'pointer',
        border: `1px solid ${workspaceColors.hairline}`,
        borderRadius: 1.5,
        bgcolor: workspaceColors.canvasBg,
        p: 2,
        display: 'flex',
        flexDirection: 'column',
        gap: 1.25,
        transition: 'border-color 160ms ease, background-color 160ms ease',
        '&:hover': {
          borderColor: workspaceAccent.ink,
          bgcolor: workspaceColors.chipBg,
        },
        '&:focus-visible': {
          outline: `2px solid ${workspaceAccent.ink}`,
          outlineOffset: 1,
        },
      }}
    >
      <Stack direction="row" spacing={1.25} alignItems="center">
        <PresetIcon preset={preset} />
        <Stack sx={{ minWidth: 0, flex: 1 }} spacing={0.25}>
          <Typography
            sx={{
              fontSize: 14,
              fontWeight: 500,
              color: workspaceText.primary,
              letterSpacing: '-0.005em',
              overflow: 'hidden',
              textOverflow: 'ellipsis',
              whiteSpace: 'nowrap',
            }}
            title={preset.displayName}
          >
            {preset.displayName}
          </Typography>
          <Typography
            sx={{
              fontSize: '0.6875rem',
              fontWeight: 600,
              letterSpacing: '0.08em',
              textTransform: 'uppercase',
              color: workspaceText.faint,
            }}
          >
            {label}
          </Typography>
        </Stack>
      </Stack>
      <Typography
        sx={{
          fontSize: 12.5,
          color: workspaceText.muted,
          lineHeight: 1.5,
          display: '-webkit-box',
          WebkitLineClamp: 2,
          WebkitBoxOrient: 'vertical',
          overflow: 'hidden',
          minHeight: 36,
        }}
      >
        {preset.description}
      </Typography>
    </Box>
  )
}

function PresetIcon({ preset }: { preset: ServicePresetDto }) {
  const categoryKey = normalizeCategory(preset.category)
  const IconComp = CATEGORY_ICON[categoryKey]
  return (
    <Box
      sx={{
        width: 32,
        height: 32,
        borderRadius: 1,
        display: 'flex',
        alignItems: 'center',
        justifyContent: 'center',
        color: CATEGORY_COLOR[categoryKey],
        bgcolor: 'rgba(0,0,0,0.04)',
      }}
    >
      <IconComp sx={{ fontSize: 18 }} />
    </Box>
  )
}

const CATEGORY_ICON: Record<CategoryFilter, typeof StorageIcon> = {
  all: ExtensionIcon,
  backend: MemoryIcon,
  frontend: ExtensionIcon,
  database: StorageIcon,
  worker: QueueIcon,
  other: FolderIcon,
}

const CATEGORY_COLOR: Record<CategoryFilter, string> = {
  all: workspaceText.muted,
  backend: workspaceAccent.ink,
  frontend: '#5B7F8F',
  database: workspaceRuntime.failed,
  worker: workspaceRuntime.booting,
  other: workspaceText.muted,
}

function normalizeCategory(raw: string): CategoryFilter {
  const lower = (raw ?? '').toLowerCase()
  if (
    lower === 'backend' ||
    lower === 'frontend' ||
    lower === 'database' ||
    lower === 'worker' ||
    lower === 'other'
  ) {
    return lower
  }
  return 'other'
}

// Mention to keep the dependency live; otherwise tree-shake removes the icon.
void MailOutlineIcon

/**
 * Build a fresh {@link ServiceInstance} from a preset definition, seeded with
 * each parameter's default value. The expander on the backend will resolve
 * any remaining defaults / required-but-empty checks; we only seed what the
 * preset already encodes.
 */
function buildInstanceFromPreset(preset: ServicePresetDto): ServiceInstance {
  const values: Record<string, string | number | boolean> = {}
  for (const param of preset.parameters ?? []) {
    if (param.defaultValue != null && param.defaultValue !== '') {
      values[param.key] = coerceDefault(param.type, param.defaultValue)
    }
  }
  return {
    kind: preset.slug,
    name: preset.slug,
    values,
  }
}

function coerceDefault(
  type: string,
  raw: string,
): string | number | boolean {
  if (type === 'Integer') {
    const n = Number(raw)
    if (!Number.isNaN(n)) return n
  }
  if (type === 'Boolean') {
    if (raw.toLowerCase() === 'true') return true
    if (raw.toLowerCase() === 'false') return false
  }
  return raw
}

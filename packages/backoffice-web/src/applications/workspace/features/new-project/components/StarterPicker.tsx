import { useMemo } from 'react'
import { Box, CircularProgress, Stack, Typography, alpha } from '@mui/material'
import RocketLaunchIcon from '@mui/icons-material/RocketLaunch'
import LinkIcon from '@mui/icons-material/Link'
import FolderOpenIcon from '@mui/icons-material/FolderOpenOutlined'
import BoltIcon from '@mui/icons-material/Bolt'
import CodeIcon from '@mui/icons-material/Code'
import type { ProjectTemplateListItem } from '../../../../../api/queries-commands'
import {
  captionSx,
  sectionTitleSx,
  workspaceAccent,
  workspaceFontFamily,
  workspaceRuntime,
  workspaceText,
} from '../../../shared'

/**
 * A "starting point" the user has chosen on the New Project page. Either:
 *  - a curated Starter (a {@link ProjectTemplateListItem} from the global
 *    {@code /api/project-templates} endpoint), in which case we submit with
 *    {@code templateId} populated;
 *  - the pinned "Empty project" — a pure no-op that preserves the legacy
 *    flow bit-for-bit ({@code templateId} stays undefined);
 *  - the pinned "Start from GitHub URL" — swaps the lower section to a
 *    single URL input.
 */
export type StartingPoint =
  | { kind: 'starter'; template: ProjectTemplateListItem }
  | { kind: 'empty' }
  | { kind: 'github-url' }

interface StarterPickerProps {
  templates: ProjectTemplateListItem[]
  isLoading: boolean
  errorMessage: string | null
  selected: StartingPoint | null
  onSelect: (next: StartingPoint) => void
}

/**
 * Map a starter's {@code iconKey} to a Material icon. We only know about a
 * tiny set of seeded keys; everything else falls back to the generic
 * RocketLaunch icon used by the super-admin Starters surface.
 */
function IconForKey({ iconKey }: { iconKey: string | null | undefined }) {
  const k = (iconKey ?? '').toLowerCase()
  const sx = { fontSize: 22, color: 'text.secondary' as const }
  if (k === 'empty' || k === 'blank') return <FolderOpenIcon sx={sx} />
  if (k === 'react' || k === 'vite') return <BoltIcon sx={sx} />
  if (k === 'rails' || k === 'ruby') return <CodeIcon sx={sx} />
  return <RocketLaunchIcon sx={sx} />
}

interface TileProps {
  selected: boolean
  onClick: () => void
  icon: React.ReactNode
  title: string
  description: string
  pinned?: boolean
}

function Tile({ selected, onClick, icon, title, description, pinned }: TileProps) {
  return (
    <Box
      onClick={onClick}
      role="button"
      tabIndex={0}
      onKeyDown={(e) => {
        if (e.key === 'Enter' || e.key === ' ') {
          e.preventDefault()
          onClick()
        }
      }}
      sx={{
        cursor: 'pointer',
        display: 'flex',
        flexDirection: 'column',
        gap: 1.25,
        p: 2,
        minHeight: 124,
        backgroundColor: pinned
          ? alpha('#000', 0.02)
          : 'instrument.inputBg',
        border: 1,
        borderColor: selected ? 'instrument.accent' : 'instrument.hairline',
        borderRadius: 2,
        outline: 'none',
        transition:
          'border-color 120ms ease, background-color 120ms ease, transform 120ms ease',
        boxShadow: selected
          ? `0 0 0 1px ${workspaceAccent.ink}`
          : 'none',
        '&:hover': {
          borderColor: selected ? workspaceAccent.ink : alpha('#000', 0.18),
          backgroundColor: pinned ? alpha('#000', 0.03) : 'instrument.inputBg',
        },
        '&:focus-visible': {
          borderColor: workspaceAccent.ink,
          // Was `alpha(workspaceAccent.ink, 0.25)`, which fails on a CSS-var
          // string — `workspaceAccent.border` is the pre-baked ink-overlay
          // ring (rgba(29,29,31,0.25) light / rgba(255,255,255,0.25) dark).
          boxShadow: `0 0 0 2px ${workspaceAccent.border}`,
        },
      }}
    >
      <Box
        sx={{
          display: 'flex',
          alignItems: 'center',
          gap: 1,
        }}
      >
        <Box
          sx={{
            width: 32,
            height: 32,
            borderRadius: 1.5,
            display: 'flex',
            alignItems: 'center',
            justifyContent: 'center',
            backgroundColor: 'instrument.chipBg',
            border: 1,
            borderColor: 'instrument.hairline',
          }}
        >
          {icon}
        </Box>
        <Typography
          sx={{
            fontFamily: workspaceFontFamily.sans,
            fontWeight: 500,
            fontSize: '0.9375rem',
            color: workspaceText.primary,
            letterSpacing: '-0.005em',
            flex: 1,
            minWidth: 0,
            overflow: 'hidden',
            textOverflow: 'ellipsis',
            whiteSpace: 'nowrap',
          }}
        >
          {title}
        </Typography>
      </Box>
      <Typography
        sx={{
          ...captionSx,
          display: '-webkit-box',
          WebkitLineClamp: 2,
          WebkitBoxOrient: 'vertical',
          overflow: 'hidden',
          lineHeight: 1.45,
          minHeight: '2.6em',
        }}
      >
        {description}
      </Typography>
    </Box>
  )
}

/**
 * Top section of the New Project page: a grid of "starting points" that
 * lets the user pick a curated Starter, paste their own GitHub URL, or
 * fall through to the legacy empty-project flow.
 *
 * <p>The pinned Empty / GitHub-URL tiles appear FIRST so the zero-friction
 * paths are above the fold even before the templates query resolves.
 * Templates follow, ordered by {@code sortOrder} then {@code name}; the
 * seeded {@code empty} slug is filtered out to avoid duplicating the pinned
 * Empty tile.
 */
export function StarterPicker({
  templates,
  isLoading,
  errorMessage,
  selected,
  onSelect,
}: StarterPickerProps) {
  const visibleTemplates = useMemo(() => {
    return [...templates]
      .filter((t) => t.slug?.toLowerCase() !== 'empty')
      .filter((t) => !t.isArchived)
      .sort((a, b) => {
        if (a.sortOrder !== b.sortOrder) return a.sortOrder - b.sortOrder
        return a.name.localeCompare(b.name)
      })
  }, [templates])

  const isEmptySelected = selected?.kind === 'empty'
  const isUrlSelected = selected?.kind === 'github-url'
  const selectedTemplateId =
    selected?.kind === 'starter' ? selected.template.id : null

  return (
    <Stack spacing={1.5}>
      <Box>
        <Typography sx={sectionTitleSx}>Choose a starting point</Typography>
        <Typography sx={{ ...captionSx, mt: 0.25 }}>
          Pick a curated Starter, paste any GitHub repo URL, or start from an
          empty project.
        </Typography>
      </Box>

      <Box
        sx={{
          display: 'grid',
          gridTemplateColumns: {
            xs: '1fr',
            sm: 'repeat(2, 1fr)',
            md: 'repeat(3, 1fr)',
          },
          gap: 1.5,
        }}
      >
        <Tile
          pinned
          selected={isEmptySelected}
          onClick={() => onSelect({ kind: 'empty' })}
          icon={<FolderOpenIcon sx={{ fontSize: 22, color: 'text.secondary' }} />}
          title="Empty project"
          description="Skip starters. Connect an existing repo or create a brand-new empty one — the original New Project flow."
        />

        <Tile
          pinned
          selected={isUrlSelected}
          onClick={() => onSelect({ kind: 'github-url' })}
          icon={<LinkIcon sx={{ fontSize: 22, color: 'text.secondary' }} />}
          title="Start from GitHub URL"
          description="Paste any GitHub repo URL. We clone it as-is — bring-your-own template, no curation required."
        />

        {visibleTemplates.map((tpl) => (
          <Tile
            key={tpl.id}
            selected={selectedTemplateId === tpl.id}
            onClick={() => onSelect({ kind: 'starter', template: tpl })}
            icon={<IconForKey iconKey={tpl.iconKey} />}
            title={tpl.name}
            description={
              tpl.description?.trim().length
                ? tpl.description
                : `${tpl.sourceRepoOwner}/${tpl.sourceRepoName}`
            }
          />
        ))}
      </Box>

      {isLoading && visibleTemplates.length === 0 ? (
        <Stack direction="row" spacing={1} alignItems="center" sx={{ pt: 0.5 }}>
          <CircularProgress size={14} sx={{ color: workspaceText.muted }} />
          <Typography sx={captionSx}>Loading starters…</Typography>
        </Stack>
      ) : null}

      {errorMessage ? (
        <Typography
          sx={{
            ...captionSx,
            color: workspaceRuntime.failed,
          }}
        >
          {errorMessage}
        </Typography>
      ) : null}
    </Stack>
  )
}

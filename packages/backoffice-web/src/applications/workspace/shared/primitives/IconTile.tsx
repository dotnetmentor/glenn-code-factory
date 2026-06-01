/**
 * IconTile — small tone-colored icon tile used as the leading glyph on
 * timeline rows, sysstats card headers, and anywhere an event/metric needs a
 * compact "what kind of thing is this" badge.
 *
 * <p>Visually a soft-tinted rounded square with a tone-colored icon centered
 * inside. Mirrors the prototype's {@code TimelineRow} icon tile: a 22px tile,
 * 6px radius, a soft semantic background, and a 12px colored icon. The
 * {@code mute} tone falls back to the neutral chip surface + muted text so
 * "nothing special happened" rows read calm.
 *
 * <pre>
 *   &lt;IconTile tone="ok" icon={PlayArrowIcon} /&gt;
 *   &lt;IconTile tone="err" icon={CloseIcon} title="Apply failed" /&gt;
 * </pre>
 *
 * <p>Colors come from {@link semanticTokens} / {@link surfaceTokens}, so the
 * tile flips light → dark with the workspace.
 */
import { Box } from '@mui/material'
import type { SvgIconComponent } from '@mui/icons-material'
import {
  semanticTokens,
  surfaceTokens,
  workspaceText,
} from '../designTokens'

/** Semantic tone driving the tile's background + icon color. */
export type IconTileTone = 'ok' | 'err' | 'warn' | 'mute'

export interface IconTileProps {
  /** Icon component rendered centered in the tile. */
  icon: SvgIconComponent
  /** Semantic tone. Defaults to {@code 'mute'} (neutral chip surface). */
  tone?: IconTileTone
  /** Pixel size of the tile. Defaults to 22 (the prototype timeline size). */
  size?: number
  /** Optional tooltip / accessible title forwarded to the tile. */
  title?: string
}

interface TonePresentation {
  bg: string
  color: string
}

function presentationFor(tone: IconTileTone): TonePresentation {
  switch (tone) {
    case 'ok':
      return { bg: semanticTokens.successSoft, color: semanticTokens.success }
    case 'err':
      return { bg: semanticTokens.errorSoft, color: semanticTokens.error }
    case 'warn':
      return { bg: semanticTokens.warningSoft, color: semanticTokens.warning }
    case 'mute':
    default:
      return { bg: surfaceTokens.chipBg, color: workspaceText.muted }
  }
}

export function IconTile({ icon: Icon, tone = 'mute', size = 22, title }: IconTileProps) {
  const { bg, color } = presentationFor(tone)
  // Icon scales with the tile but stays comfortably inside it — the prototype
  // pairs a 22px tile with a 12px glyph.
  const iconSize = Math.round((size * 12) / 22)
  return (
    <Box
      aria-hidden
      title={title}
      sx={{
        width: size,
        height: size,
        borderRadius: '6px',
        backgroundColor: bg,
        color,
        display: 'inline-flex',
        alignItems: 'center',
        justifyContent: 'center',
        flexShrink: 0,
      }}
    >
      <Icon sx={{ fontSize: iconSize, color }} />
    </Box>
  )
}

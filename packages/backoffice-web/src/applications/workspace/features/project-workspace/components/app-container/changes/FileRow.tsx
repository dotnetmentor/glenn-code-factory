import { Box, Stack, Tooltip, Typography } from '@mui/material'
import { appContainerTokens } from '../tokens'
import { workspaceFontFamily } from '@/applications/workspace/shared/designTokens'
import type { ChangedFile } from '../../../../../../../api/queries-commands'

interface FileRowProps {
  file: ChangedFile
  selected: boolean
  onSelect: (path: string) => void
}

/**
 * Map the wire status string to the single-letter glyph the spec calls
 * for. The list is the calm "ledger" — no traffic-light colour, no
 * dot. The glyph stays muted until the row is selected, when both the
 * glyph and the path tint to the workspace accent.
 */
function statusGlyph(status: string): string {
  switch (status) {
    case 'added':
      return 'A'
    case 'modified':
    case 'binary-modified':
      return 'M'
    case 'deleted':
      return 'D'
    case 'renamed':
      return 'R'
    case 'untracked':
      return 'U'
    default:
      // Forward-compatible fallback: take the first uppercased letter.
      // Keeps the row meaningful even if the daemon adds a status the
      // frontend hasn't been re-deployed for yet.
      return (status[0] ?? '?').toUpperCase()
  }
}

/**
 * One file row in the {@link FileList}. 28px tall, three columns:
 * status glyph, path (with right-anchored ellipsis so the *end* of the
 * path stays visible — usually the most informative bit), and the
 * additions/deletions chips.
 *
 * <p>Renames render as {@code old/path → new/path}, again with each
 * side right-anchored so the filename of each side is preserved when
 * the row is narrow.</p>
 */
export function FileRow({ file, selected, onSelect }: FileRowProps) {
  const glyph = statusGlyph(file.status)
  const isRename = file.status === 'renamed' && !!file.oldPath

  const path = (
    <Box
      sx={{
        flex: 1,
        minWidth: 0,
        display: 'flex',
        alignItems: 'center',
        gap: 0.5,
        overflow: 'hidden',
      }}
    >
      {isRename ? (
        <>
          <PathSegment text={file.oldPath ?? ''} />
          <Typography
            component="span"
            sx={{
              flexShrink: 0,
              color: 'inherit',
              opacity: 0.55,
              fontFamily: workspaceFontFamily.mono,
              fontSize: '0.75rem',
            }}
          >
            →
          </Typography>
          <PathSegment text={file.path} />
        </>
      ) : (
        <PathSegment text={file.path} />
      )}
    </Box>
  )

  const showStats = !file.isBinary
  return (
    <Box
      role="button"
      tabIndex={0}
      onClick={() => onSelect(file.path)}
      onKeyDown={(e) => {
        if (e.key === 'Enter' || e.key === ' ') {
          e.preventDefault()
          onSelect(file.path)
        }
      }}
      aria-pressed={selected}
      sx={{
        height: 28,
        px: 1,
        display: 'flex',
        alignItems: 'center',
        gap: 1,
        cursor: 'pointer',
        backgroundColor: selected
          ? appContainerTokens.accentSurface
          : 'transparent',
        color: selected
          ? appContainerTokens.accent
          : appContainerTokens.textPrimary,
        transition: 'background-color 150ms ease, color 150ms ease',
        '&:hover': {
          backgroundColor: selected
            ? appContainerTokens.accentSurfaceHover
            : 'rgba(0, 0, 0, 0.02)',
        },
        '&:focus-visible': {
          outline: `2px solid ${appContainerTokens.accent}`,
          outlineOffset: -2,
        },
      }}
    >
      <Box
        component="span"
        sx={{
          width: 16,
          flexShrink: 0,
          textAlign: 'center',
          fontFamily: workspaceFontFamily.mono,
          fontSize: '0.75rem',
          color: selected ? appContainerTokens.accent : appContainerTokens.textMuted,
          letterSpacing: 0,
        }}
      >
        {glyph}
      </Box>
      {path}
      {showStats && (
        <Stack
          direction="row"
          spacing={0.75}
          sx={{
            flexShrink: 0,
            color: appContainerTokens.textMuted,
            fontFamily: workspaceFontFamily.mono,
            fontSize: '0.7rem',
          }}
        >
          {file.additions > 0 && (
            <Box component="span">+{file.additions}</Box>
          )}
          {file.deletions > 0 && (
            <Box component="span">−{file.deletions}</Box>
          )}
        </Stack>
      )}
    </Box>
  )
}

interface PathSegmentProps {
  text: string
}

/**
 * A single path string with right-anchored ellipsis. We achieve that by
 * setting {@code direction: rtl} so the truncation happens on the left
 * (i.e. the start of the path) while still rendering left-to-right — a
 * common trick that keeps the filename visible when the row is narrow.
 * The {@code ::before} unicode-bidi reset preserves the actual visual
 * order of the characters even though direction is rtl.
 */
function PathSegment({ text }: PathSegmentProps) {
  return (
    <Tooltip title={text} enterDelay={500}>
      <Box
        sx={{
          flex: '1 1 auto',
          minWidth: 0,
          fontFamily: workspaceFontFamily.mono,
          fontSize: '0.75rem',
          color: 'inherit',
          letterSpacing: 0,
          whiteSpace: 'nowrap',
          overflow: 'hidden',
          textOverflow: 'ellipsis',
          direction: 'rtl',
          textAlign: 'left',
          // Force ltr rendering of the actual characters within the rtl
          // container so paths read normally.
          '& > bdi': {
            unicodeBidi: 'plaintext',
          },
        }}
      >
        <bdi>{text}</bdi>
      </Box>
    </Tooltip>
  )
}

import { Box, Tooltip } from '@mui/material'
import LocalFireDepartmentIcon from '@mui/icons-material/LocalFireDepartment'
import LocalFireDepartmentOutlinedIcon from '@mui/icons-material/LocalFireDepartmentOutlined'
import { useYoloMode } from '../hooks/useYoloMode'

import {
  workspaceAccent,
  workspaceRuntime,
  workspaceText,
} from '../../../shared/designTokens'

const COLOR_MUTED = workspaceText.disabled
const COLOR_PRIMARY = workspaceText.primary
const COLOR_ACCENT = workspaceAccent.ink
const COLOR_YOLO_ON = workspaceRuntime.booting

interface ComposerYoloToggleProps {
  /**
   * The active conversation. {@code null} means draft / empty state — there
   * is no localStorage key to persist against yet, so the toggle hides
   * until the first message commits a real conversation id. This matches
   * the {@code ComposerModelPickerInline} convention so both widgets pop
   * into existence at the same moment.
   */
  conversationId: string | null
}

/**
 * Ambient, ultra-quiet per-conversation "yolo" toggle.
 *
 * <p>Renders as a small block of muted text + a small flame icon tucked
 * directly next to {@link ComposerModelPickerInline} under the chat
 * composer. Click flips the per-conversation flag (persisted in
 * localStorage via {@link useYoloMode}); the send path reads the same key
 * via {@code readYoloMode} at submit time so the choice threads into the
 * very next {@code SubmitPrompt} payload as {@code yolo: boolean}.</p>
 *
 * <p><b>On state (default):</b> filled flame in the accent colour with the
 * "yolo" label slightly warmer — reads as "armed". Tooltip explains the
 * agent will skip permission prompts.</p>
 *
 * <p><b>Off state:</b> outlined flame at the muted text colour with the
 * "yolo" label dimmed — reads as "guarded". Tooltip explains the agent
 * will ask before each tool use.</p>
 *
 * <p>Both states honour the same quiet aesthetic as the model picker:
 * 0.6875rem text, 500 weight, no border, no background until hover, gentle
 * colour shift on hover only.</p>
 */
export function ComposerYoloToggle({ conversationId }: ComposerYoloToggleProps) {
  const { yolo, setYolo } = useYoloMode(conversationId)

  // Hide in draft mode for the same reason the model picker does — without
  // a conversation id there is nowhere to persist the choice, and showing
  // a toggle that silently resets next render would be confusing.
  if (!conversationId) return null

  const tooltip = yolo ? 'Skip permission prompts' : 'Ask before tool use'

  return (
    <Tooltip title={tooltip} enterDelay={400}>
      <Box
        component="button"
        type="button"
        onClick={() => setYolo(!yolo)}
        aria-pressed={yolo}
        aria-label={yolo ? 'Yolo mode on' : 'Yolo mode off'}
        sx={{
          display: 'inline-flex',
          alignItems: 'center',
          gap: 0.25,
          border: 0,
          background: 'none',
          padding: '2px 4px',
          fontSize: '0.6875rem',
          fontWeight: 500,
          color: yolo ? COLOR_PRIMARY : COLOR_MUTED,
          cursor: 'pointer',
          letterSpacing: '-0.005em',
          fontFamily: 'inherit',
          lineHeight: 1.3,
          borderRadius: 0.5,
          transition: 'color 120ms ease, opacity 120ms ease',
          opacity: yolo ? 1 : 0.85,
          '&:hover': {
            color: COLOR_PRIMARY,
            opacity: 1,
          },
          '&:focus-visible': {
            outline: `1px solid ${COLOR_ACCENT}`,
            outlineOffset: 1,
          },
        }}
      >
        {yolo ? (
          <LocalFireDepartmentIcon
            sx={{ fontSize: 12, color: COLOR_YOLO_ON }}
          />
        ) : (
          <LocalFireDepartmentOutlinedIcon
            sx={{ fontSize: 12, opacity: 0.7 }}
          />
        )}
        <Box component="span" sx={{ whiteSpace: 'nowrap' }}>
          yolo
        </Box>
      </Box>
    </Tooltip>
  )
}

import { useEffect, useRef } from 'react'
import { Box, IconButton, Stack, Typography } from '@mui/material'
import KeyboardArrowDownIcon from '@mui/icons-material/KeyboardArrowDown'
import SettingsIcon from '@mui/icons-material/Settings'
import { RuntimeState } from '@/api/queries-commands'
import {
  surfaceTokens,
  chromeTokens,
  workspaceColors,
  workspaceFontFamily,
  semanticTokens,
  workspaceChromeHeight,
} from '../../../shared/designTokens'
import { StatusDot } from '../../../shared/primitives'
import type { ChatItem, MovieState } from '../movie/script'

const SANS = workspaceFontFamily.sans
const MONO = workspaceFontFamily.mono

function ToolChip({ label, detail, tone }: { label: string; detail: string; tone?: 'default' | 'warn' | 'success' }) {
  const dot =
    tone === 'warn' ? semanticTokens.warning : tone === 'success' ? semanticTokens.success : surfaceTokens.textFaint
  return (
    <Stack
      direction="row"
      alignItems="center"
      spacing={1}
      sx={{
        alignSelf: 'flex-start',
        px: 1.25,
        py: 0.65,
        borderRadius: 1.25,
        backgroundColor: workspaceColors.chipBg,
        border: `1px solid ${surfaceTokens.hairline}`,
      }}
    >
      <Box sx={{ width: 6, height: 6, borderRadius: '50%', backgroundColor: dot, flexShrink: 0 }} />
      <Typography sx={{ fontFamily: MONO, fontSize: '0.78rem', fontWeight: 600, color: surfaceTokens.textPrimary }}>
        {label}
      </Typography>
      <Typography sx={{ fontFamily: MONO, fontSize: '0.78rem', color: surfaceTokens.textFaint }}>
        {detail}
      </Typography>
    </Stack>
  )
}

function Bubble({ role, text, typing }: { role: 'user' | 'assistant'; text: string; typing?: boolean }) {
  const isUser = role === 'user'
  return (
    <Box sx={{ display: 'flex', justifyContent: isUser ? 'flex-end' : 'flex-start' }}>
      <Box
        sx={{
          maxWidth: '88%',
          px: 1.75,
          py: 1.25,
          borderRadius: 2,
          ...(isUser
            ? { backgroundColor: workspaceColors.bubbleUser, border: `1px solid ${surfaceTokens.hairline}` }
            : { backgroundColor: chromeTokens.bubbleAssistant }),
        }}
      >
        <Typography
          sx={{
            fontFamily: SANS,
            fontSize: '0.9rem',
            lineHeight: 1.5,
            color: surfaceTokens.textPrimary,
            whiteSpace: 'pre-wrap',
          }}
        >
          {text}
          {typing && (
            <Box
              component="span"
              sx={{
                display: 'inline-block',
                width: '2px',
                height: '1em',
                ml: '1px',
                verticalAlign: '-2px',
                backgroundColor: surfaceTokens.textPrimary,
                animation: 'wsCaret 1s steps(1) infinite',
                '@keyframes wsCaret': { '50%': { opacity: 0 } },
              }}
            />
          )}
        </Typography>
      </Box>
    </Box>
  )
}

/**
 * Scripted chat transcript that mirrors the real workspace chat's visual
 * language (bubbles, tool chips) without re-driving the live ChatCanvas. Fed
 * entirely by {@link MovieState}.
 */
export function DemoChat({ state }: { state: MovieState }) {
  const scrollRef = useRef<HTMLDivElement>(null)

  // Stick to bottom as items stream in — same instinct as the real canvas.
  useEffect(() => {
    const el = scrollRef.current
    if (el) el.scrollTop = el.scrollHeight
  }, [state.chat.length, state.typedUser])

  return (
    <Box sx={{ display: 'flex', flexDirection: 'column', height: '100%', minWidth: 0 }}>
      {/* Chrome strip — conversation title + picker chevron, runtime pill, cog.
          Mirrors the real ChatChrome on the shared y-grid. */}
      <Stack
        direction="row"
        alignItems="center"
        spacing={0.5}
        sx={{
          height: workspaceChromeHeight,
          flexShrink: 0,
          px: 1.5,
          borderBottom: `1px solid ${surfaceTokens.hairline}`,
        }}
      >
        <Stack direction="row" alignItems="center" spacing={0.5} sx={{ minWidth: 0, px: 0.75, py: 0.4, borderRadius: 1.25, '&:hover': { backgroundColor: chromeTokens.rowHover } }}>
          <Typography noWrap sx={{ fontFamily: SANS, fontWeight: 600, fontSize: '0.875rem', color: surfaceTokens.textPrimary }}>
            Waitlist landing
          </Typography>
          <KeyboardArrowDownIcon sx={{ fontSize: 16, color: surfaceTokens.textFaint }} />
        </Stack>
        <Box sx={{ flex: 1 }} />
        <Stack direction="row" alignItems="center" spacing={0.6} sx={{ px: 0.9, py: 0.4, borderRadius: 999, backgroundColor: semanticTokens.successSoft }}>
          <StatusDot state={RuntimeState.Online} size={7} hideTooltip />
          <Typography sx={{ fontFamily: MONO, fontSize: '0.7rem', fontWeight: 600, color: surfaceTokens.textMuted }}>
            sandbox
          </Typography>
        </Stack>
        <IconButton size="small" disabled sx={{ color: surfaceTokens.textFaint }}>
          <SettingsIcon sx={{ fontSize: 16 }} />
        </IconButton>
      </Stack>

      {/* Transcript */}
      <Box ref={scrollRef} sx={{ flex: 1, overflowY: 'auto', px: 2, py: 2 }}>
        <Stack spacing={1.5}>
          {state.chat.map((item: ChatItem, i) => {
            if (item.kind === 'tool') {
              return <ToolChip key={i} label={item.label} detail={item.detail} tone={item.tone} />
            }
            // The user bubble reveals via the typewriter substring from state.
            const isFirstUser = item.role === 'user' && i === 0
            return (
              <Bubble
                key={i}
                role={item.role}
                text={isFirstUser ? state.typedUser : item.text}
                typing={isFirstUser && state.userTyping}
              />
            )
          })}
        </Stack>
      </Box>

      {/* Composer (static, decorative) */}
      <Box sx={{ flexShrink: 0, p: 1.5, borderTop: `1px solid ${surfaceTokens.hairline}` }}>
        <Box
          sx={{
            px: 1.75,
            py: 1.25,
            borderRadius: 2,
            backgroundColor: workspaceColors.inputBg,
            border: `1px solid ${surfaceTokens.hairline}`,
          }}
        >
          <Typography sx={{ fontFamily: SANS, fontSize: '0.875rem', color: surfaceTokens.textFaint }}>
            {state.repoConnected ? 'Describe what you want to build…' : 'Connecting your repo…'}
          </Typography>
        </Box>
      </Box>
    </Box>
  )
}

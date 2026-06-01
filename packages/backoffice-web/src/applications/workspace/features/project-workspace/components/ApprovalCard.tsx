/**
 * ApprovalCard — the inline canUseTool approval surface inside a turn (P5 of
 * the agent-sdk-permissions spec, Scene 4).
 *
 * <p>When the daemon's {@code canUseTool} callback fires, the backend relays
 * a {@code PermissionRequested} fan-out over AgentHub keyed by the SDK's
 * {@code toolUseId}. {@link ChatCanvas} subscribes, stashes the payload, and
 * renders this card inline in the transcript — between turn-trace rows, with
 * the same visual weight as a tool-call trace row. NOT a modal, NOT a dialog.
 * The user picks one of four actions; the resulting {@code ResolvePermission}
 * rides back through the typed hub proxy, the card collapses into a compact
 * "Approved" / "Denied" trace row in place, and the agent continues.</p>
 *
 * <p>Visual language mirrors {@code TurnTrace}: white chrome background, the
 * same hairline borders, the same monospace stack for tool inputs, the same
 * "Show more" affordance for long JSON payloads. The four actions sit in a
 * single row; the "Deny with feedback" path expands a multiline TextField
 * underneath the row only when the user picks that option (no permanent
 * textarea cluttering the calm idle state).</p>
 */
import { useMemo, useState } from 'react'
import { Box, Button, Stack, TextField, Typography } from '@mui/material'
import { keyframes } from '@mui/system'
import type {
  PermissionRequestedPayload,
  ResolvePermissionPayload,
} from '@/generated/signalr/Source.Features.SignalR.Contracts'

import {
  workspaceAccent,
  workspaceRuntime,
  workspaceText,
} from '../../../shared/designTokens'

const tokens = {
  textPrimary: workspaceText.primary,
  textMuted: workspaceText.muted,
  textFaint: workspaceText.faint,
  accent: workspaceAccent.ink,
  runtimeFailed: workspaceRuntime.failed,
} as const

const fadeIn = keyframes`
  from { opacity: 0; transform: translateY(2px); }
  to   { opacity: 1; transform: translateY(0); }
`

const MONO_STACK = '"SF Mono", "Menlo", "Monaco", "Consolas", monospace'
const SANS_STACK =
  '"Inter", -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, sans-serif'

// Mirror the TurnTrace "Show more" threshold so a card with a multi-line
// Bash command or large JSON input doesn't dominate the transcript.
const PREVIEW_CHAR_LIMIT = 500

// ── Decision strings (must mirror the daemon's PermissionGateway parser) ───
//
// The backend forwards the resolution to the daemon's pending canUseTool
// continuation; the daemon decodes these exact strings into SDK responses.
// Kept here as constants so a typo on either side fails loudly at the seam.
export const PermissionDecision = {
  Approve: 'approve',
  ApproveAlwaysSession: 'approveAlwaysSession',
  Deny: 'deny',
  DenyWithFeedback: 'denyWithFeedback',
} as const
export type PermissionDecisionValue =
  (typeof PermissionDecision)[keyof typeof PermissionDecision]

// ── Tool-input pretty-printing ─────────────────────────────────────────────
//
// The daemon stringifies the SDK's `tool_use.input` (a `JsonElement`) before
// the hub fan-out — Tapper rejects JsonElement on the wire. We always parse,
// then format per tool:
//   * Bash → just the `command` string in monospace.
//   * Write/Edit → file path + a short preview of `content` / `new_string`.
//   * Anything else → pretty-printed JSON, truncated past PREVIEW_CHAR_LIMIT.
interface PreviewBlock {
  label: string
  text: string
  /** When true, force monospace + collapsible block; false → plain prose. */
  mono: boolean
}

function buildPreview(toolName: string, toolInputRaw: string): PreviewBlock[] {
  let parsed: unknown = null
  try {
    parsed = JSON.parse(toolInputRaw)
  } catch {
    // Bad JSON from the daemon shouldn't crash the card — show the raw blob.
    return [{ label: 'input', text: toolInputRaw, mono: true }]
  }
  if (parsed === null || typeof parsed !== 'object') {
    return [{ label: 'input', text: String(parsed), mono: true }]
  }
  const obj = parsed as Record<string, unknown>

  // Bash → command-first view. Description (when present) is muted prose
  // above the command itself.
  if (toolName === 'Bash') {
    const command = typeof obj.command === 'string' ? obj.command : ''
    const description =
      typeof obj.description === 'string' ? obj.description : ''
    const blocks: PreviewBlock[] = []
    if (description) {
      blocks.push({ label: 'description', text: description, mono: false })
    }
    blocks.push({
      label: 'command',
      text: command || '(empty)',
      mono: true,
    })
    return blocks
  }

  // Write → file path + truncated content preview.
  if (toolName === 'Write') {
    const filePath = typeof obj.file_path === 'string' ? obj.file_path : ''
    const content = typeof obj.content === 'string' ? obj.content : ''
    return [
      { label: 'file', text: filePath || '(unknown)', mono: true },
      { label: 'content', text: content || '(empty)', mono: true },
    ]
  }

  // Edit → file path + the new_string preview. (We don't show old_string in
  // the approval card — the user is being asked to allow the *new* state.)
  if (toolName === 'Edit') {
    const filePath = typeof obj.file_path === 'string' ? obj.file_path : ''
    const newString =
      typeof obj.new_string === 'string' ? obj.new_string : ''
    return [
      { label: 'file', text: filePath || '(unknown)', mono: true },
      { label: 'new content', text: newString || '(empty)', mono: true },
    ]
  }

  // Generic → pretty-printed JSON. (Long values fall back to the same
  // "Show more" affordance the TurnTrace CodeBlock uses.)
  return [
    {
      label: 'input',
      text: JSON.stringify(obj, null, 2),
      mono: true,
    },
  ]
}

// ── Quiet uppercase label (mirrors TurnTrace.QuietLabel) ───────────────────

function QuietLabel({ children }: { children: React.ReactNode }) {
  return (
    <Typography
      component="div"
      sx={{
        fontSize: 11,
        letterSpacing: '0.05em',
        textTransform: 'uppercase',
        color: tokens.textFaint,
        mb: 0.25,
        fontFamily: SANS_STACK,
      }}
    >
      {children}
    </Typography>
  )
}

// ── Code block (collapsible past PREVIEW_CHAR_LIMIT) ───────────────────────

function CodeBlock({ text }: { text: string }) {
  const [expanded, setExpanded] = useState(false)
  const overLimit = text.length > PREVIEW_CHAR_LIMIT
  const shown = overLimit && !expanded ? `${text.slice(0, PREVIEW_CHAR_LIMIT)}…` : text
  return (
    <Box>
      <Box
        sx={{
          fontFamily: MONO_STACK,
          fontSize: 12.5,
          lineHeight: 1.55,
          color: tokens.textPrimary,
          backgroundColor: 'instrument.codeBg',
          border: 1,
          borderColor: 'instrument.hairline',
          borderRadius: '6px',
          p: 1.25,
          whiteSpace: 'pre-wrap',
          wordBreak: 'break-word',
        }}
      >
        {shown}
      </Box>
      {overLimit && (
        <Box sx={{ mt: 0.5 }}>
          <Typography
            component="button"
            type="button"
            onClick={() => setExpanded((v) => !v)}
            sx={{
              background: 'none',
              border: 'none',
              padding: 0,
              cursor: 'pointer',
              fontSize: 12,
              color: tokens.textMuted,
              letterSpacing: '0.01em',
              fontFamily: SANS_STACK,
              transition: 'color 200ms ease',
              '&:hover': { color: tokens.textPrimary },
              '&:focus-visible': {
                outline: '1px solid rgba(0,0,0,0.12)',
                outlineOffset: 2,
                borderRadius: '2px',
              },
            }}
          >
            {expanded ? 'Show less' : 'Show more'}
          </Typography>
        </Box>
      )}
    </Box>
  )
}

// ── Public props ────────────────────────────────────────────────────────────

export interface ApprovalCardProps {
  payload: PermissionRequestedPayload
  /**
   * Fired when the user picks one of the four actions. ChatCanvas pipes the
   * payload through {@code connection.resolvePermission} and clears the entry
   * from its {@code pendingApprovals} map.
   */
  onResolve: (resolution: ResolvePermissionPayload) => void
}

// ── Component ───────────────────────────────────────────────────────────────

export function ApprovalCard({ payload, onResolve }: ApprovalCardProps) {
  const { toolUseId, toolName, toolInput, conversationId, turnId } = payload

  const previews = useMemo(
    () => buildPreview(toolName, toolInput),
    [toolName, toolInput],
  )

  // Two-stage state — once the user picks a decision we lock the buttons so
  // a double-click can't fire two resolutions before the parent removes us
  // from the pendingApprovals map.
  const [submitting, setSubmitting] = useState(false)
  // "Deny with feedback" mode — when the user clicks the 4th button we expand
  // a multiline TextField + Submit button inline below the action row.
  const [feedbackMode, setFeedbackMode] = useState(false)
  const [feedback, setFeedback] = useState('')

  const resolve = (
    decision: PermissionDecisionValue,
    feedbackText?: string,
  ) => {
    if (submitting) return
    setSubmitting(true)
    // Build the payload with only the fields the backend expects. Optional
    // conversationId / turnId pass through verbatim if the daemon supplied
    // them on the request side (they're used for future audit; not required
    // for the v1 resolve flow).
    const out: ResolvePermissionPayload = {
      toolUseId,
      decision,
      ...(feedbackText && feedbackText.trim().length > 0
        ? { feedback: feedbackText.trim() }
        : {}),
      ...(conversationId ? { conversationId } : {}),
      ...(turnId ? { turnId } : {}),
    }
    onResolve(out)
  }

  return (
    <Box
      role="region"
      aria-label={`Approval request for ${toolName}`}
      sx={{
        // Match TurnTrace's indent + hairline so the card visually nests
        // under the status row exactly like an inline trace row would.
        ml: 3,
        maxWidth: 720,
        pl: 1.5,
        pt: 0.5,
        pb: 1,
        borderLeft: 1,
        borderColor: 'instrument.hairline',
        animation: `${fadeIn} 200ms ease`,
        display: 'flex',
        flexDirection: 'column',
        gap: 1,
      }}
    >
      {/* Headline — "Claude wants to run `<toolName>`". Tool name verbatim
          in monospace so Bash / Write / Edit are visually distinct from the
          surrounding prose. */}
      <Box sx={{ display: 'flex', alignItems: 'baseline', gap: 0.75 }}>
        <Typography
          component="div"
          sx={{
            fontSize: 13,
            color: tokens.textPrimary,
            letterSpacing: '0.01em',
            fontFamily: SANS_STACK,
          }}
        >
          Claude wants to run
        </Typography>
        <Typography
          component="span"
          sx={{
            fontFamily: MONO_STACK,
            fontSize: 13,
            fontWeight: 600,
            color: tokens.accent,
          }}
        >
          {toolName}
        </Typography>
      </Box>

      {/* Body — one or more preview blocks (label + value) following the
          per-tool conventions in {@link buildPreview}. */}
      <Stack spacing={0.75}>
        {previews.map((block) => (
          <Box key={block.label}>
            <QuietLabel>{block.label}</QuietLabel>
            {block.mono ? (
              <CodeBlock text={block.text} />
            ) : (
              <Typography
                component="div"
                sx={{
                  fontSize: 13,
                  lineHeight: 1.55,
                  color: tokens.textMuted,
                  fontFamily: SANS_STACK,
                  whiteSpace: 'pre-wrap',
                  wordBreak: 'break-word',
                }}
              >
                {block.text}
              </Typography>
            )}
          </Box>
        ))}
      </Stack>

      {/* Action row — four buttons. The "Deny with feedback" button toggles
          a textarea below the row; the other three resolve immediately. */}
      <Stack
        direction="row"
        spacing={1}
        sx={{ flexWrap: 'wrap', rowGap: 1, mt: 0.5 }}
      >
        <Button
          variant="pill"
          color="primary"
          size="small"
          disabled={submitting}
          onClick={() => resolve(PermissionDecision.Approve)}
        >
          Approve
        </Button>
        <Button
          variant="outlined"
          size="small"
          disabled={submitting}
          onClick={() => resolve(PermissionDecision.ApproveAlwaysSession)}
          sx={{
            borderColor: 'instrument.hairline',
            color: tokens.textPrimary,
            textTransform: 'none',
            fontFamily: SANS_STACK,
            fontWeight: 500,
            letterSpacing: '0.01em',
            '&:hover': {
              borderColor: 'rgba(0,0,0,0.18)',
              backgroundColor: 'rgba(0,0,0,0.02)',
            },
          }}
        >
          Approve and always allow this session
        </Button>
        <Button
          variant="outlined"
          size="small"
          disabled={submitting}
          onClick={() => resolve(PermissionDecision.Deny)}
          sx={{
            borderColor: 'instrument.hairline',
            color: tokens.textMuted,
            textTransform: 'none',
            fontFamily: SANS_STACK,
            fontWeight: 500,
            letterSpacing: '0.01em',
            '&:hover': {
              borderColor: 'rgba(0,0,0,0.18)',
              backgroundColor: 'rgba(0,0,0,0.02)',
            },
          }}
        >
          Deny
        </Button>
        <Button
          variant="outlined"
          size="small"
          disabled={submitting}
          onClick={() => setFeedbackMode((v) => !v)}
          sx={{
            borderColor: 'instrument.hairline',
            color: tokens.textMuted,
            textTransform: 'none',
            fontFamily: SANS_STACK,
            fontWeight: 500,
            letterSpacing: '0.01em',
            '&:hover': {
              borderColor: 'rgba(0,0,0,0.18)',
              backgroundColor: 'rgba(0,0,0,0.02)',
            },
          }}
        >
          {feedbackMode ? 'Cancel feedback' : 'Deny with feedback…'}
        </Button>
      </Stack>

      {/* Feedback drawer — only mounted when the user picked the 4th button.
          Submit ships the text along with a denyWithFeedback decision; the
          backend forwards the feedback string into the SDK's `message` field
          on the deny response so the agent can adjust its plan. */}
      {feedbackMode && (
        <Box sx={{ mt: 0.5 }}>
          <TextField
            fullWidth
            multiline
            minRows={2}
            maxRows={6}
            size="small"
            placeholder="Tell Claude what to do instead…"
            value={feedback}
            onChange={(e) => setFeedback(e.target.value)}
            disabled={submitting}
            sx={{
              '& .MuiOutlinedInput-root': {
                fontSize: 13,
                fontFamily: SANS_STACK,
                backgroundColor: 'instrument.surface',
              },
            }}
          />
          <Stack direction="row" spacing={1} sx={{ mt: 1 }}>
            <Button
              variant="contained"
              size="small"
              disabled={submitting || feedback.trim().length === 0}
              onClick={() =>
                resolve(PermissionDecision.DenyWithFeedback, feedback)
              }
              sx={{
                backgroundColor: tokens.runtimeFailed,
                color: 'instrument.chrome',
                textTransform: 'none',
                fontFamily: SANS_STACK,
                fontWeight: 500,
                letterSpacing: '0.01em',
                boxShadow: 'none',
                '&:hover': {
                  backgroundColor: workspaceRuntime.failedHover,
                  boxShadow: 'none',
                },
              }}
            >
              Submit feedback
            </Button>
          </Stack>
        </Box>
      )}
    </Box>
  )
}

// ── Resolved-trace row ─────────────────────────────────────────────────────
//
// Tiny inline row that replaces the card after the user resolves it. Same
// indent + hairline so the layout doesn't jump. Reads like a faded tool-call
// trace row — calm, present, but explicitly past-tense.

export interface ResolvedApprovalRowProps {
  toolName: string
  decision: PermissionDecisionValue
}

export function ResolvedApprovalRow({
  toolName,
  decision,
}: ResolvedApprovalRowProps) {
  const isApproval =
    decision === PermissionDecision.Approve ||
    decision === PermissionDecision.ApproveAlwaysSession
  const label = isApproval ? 'Approved' : 'Denied'
  const color = isApproval ? tokens.textMuted : tokens.runtimeFailed
  return (
    <Box
      sx={{
        ml: 3,
        maxWidth: 720,
        pl: 1.5,
        py: 0.5,
        borderLeft: 1,
        borderColor: 'instrument.hairline',
        animation: `${fadeIn} 200ms ease`,
        display: 'flex',
        alignItems: 'baseline',
        gap: 0.75,
      }}
    >
      <Typography
        component="div"
        sx={{
          fontSize: 11,
          letterSpacing: '0.05em',
          textTransform: 'uppercase',
          color,
          fontFamily: SANS_STACK,
        }}
      >
        {label}
      </Typography>
      <Typography
        component="span"
        sx={{
          fontFamily: MONO_STACK,
          fontSize: 13,
          fontWeight: 500,
          color: tokens.textPrimary,
        }}
      >
        {toolName}
      </Typography>
      {decision === PermissionDecision.ApproveAlwaysSession && (
        <Typography
          component="span"
          sx={{
            fontSize: 12,
            color: tokens.textFaint,
            fontFamily: SANS_STACK,
            ml: 0.5,
          }}
        >
          (always allow this session)
        </Typography>
      )}
    </Box>
  )
}

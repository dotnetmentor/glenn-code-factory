import {
  useEffect,
  useRef,
  useState,
  type KeyboardEvent,
  type MouseEvent,
} from 'react'
import { Box, InputBase, Tooltip, Typography } from '@mui/material'

import { workspaceColors, workspaceText } from '../../../shared/designTokens'

const tokens = {
  textPrimary: workspaceText.primary,
  editBg: workspaceColors.codeBg,
} as const

export interface EditableTitleProps {
  /** The current persisted title. */
  value: string
  /**
   * Called when the user commits a change. May return a Promise — while it is
   * pending the title renders with a calm opacity dip. Errors thrown / rejected
   * are swallowed by this primitive (no toast); callers handle rollback in
   * their mutation onError.
   */
  onCommit: (next: string) => void | Promise<void>
  /** Accessibility label for the edit input. Defaults to "Title". */
  ariaLabel?: string
  /**
   * Visual variant. {@code sidebar} matches the conversation-row title
   * (~13px / 500 weight); {@code chrome} is one click larger (~15px / 500)
   * for the chat-chrome strip.
   */
  variant?: 'sidebar' | 'chrome'
  /** Disables click-to-edit entirely (read-only mode). */
  disabled?: boolean
  /**
   * Optional fallback rendered when {@code value} is empty AND we are not in
   * edit mode. Used for placeholder copy like "Untitled conversation".
   */
  emptyFallback?: string
  /**
   * Controlled edit mode. When provided, the primitive does not flip into
   * edit mode on its own — the parent decides when. This is how the
   * conversation sidebar wires the menu-driven Rename action: click the menu
   * item → parent sets {@code editing=true}.
   *
   * When {@code editing} is undefined the primitive is uncontrolled and
   * click-on-title enters edit mode directly (the chat-chrome usage).
   */
  editing?: boolean
  /** Called when the primitive wants to enter / leave edit mode. */
  onEditingChange?: (editing: boolean) => void
}

/**
 * Inline-editable title primitive.
 *
 * <p>Click → input (uncontrolled mode) or parent-driven (controlled mode).
 * Enter or blur commits if the value changed and is not empty. Esc reverts.
 * The component never raises an error toast — it simply snaps back to
 * {@code value} on empty / failed commits. Loading state is conveyed by a
 * subtle opacity dip, not a spinner or banner.</p>
 *
 * <p>Used by:
 * <ul>
 *   <li>The conversation sidebar (Scenario 9 — rename row, controlled mode
 *       so the menu-item "Rename" is the trigger, not a click on the row
 *       title which selects the conversation).</li>
 *   <li>The chat chrome strip (Scenario 7 — title sits left of the branch
 *       switcher and is renamed in place by clicking it).</li>
 * </ul></p>
 */
export function EditableTitle({
  value,
  onCommit,
  ariaLabel = 'Title',
  variant = 'sidebar',
  disabled = false,
  emptyFallback,
  editing: editingProp,
  onEditingChange,
}: EditableTitleProps) {
  const isControlled = editingProp !== undefined
  const [internalEditing, setInternalEditing] = useState(false)
  const editing = isControlled ? !!editingProp : internalEditing

  const [draft, setDraft] = useState(value)
  const [committing, setCommitting] = useState(false)
  const inputRef = useRef<HTMLInputElement | null>(null)

  const setEditing = (next: boolean) => {
    if (!isControlled) setInternalEditing(next)
    onEditingChange?.(next)
  }

  // Keep draft in sync when the upstream value changes while we are NOT
  // editing — covers the case where SignalR or a refetch updates the title
  // out-of-band.
  useEffect(() => {
    if (!editing) setDraft(value)
  }, [value, editing])

  // When we (re-)enter edit mode, seed the draft with the current value so
  // Esc reverts to truth rather than a stale draft.
  useEffect(() => {
    if (editing) setDraft(value)
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [editing])

  useEffect(() => {
    if (editing && inputRef.current) {
      inputRef.current.focus()
      inputRef.current.select()
    }
  }, [editing])

  const beginEdit = (e: MouseEvent<HTMLElement>) => {
    if (disabled) return
    e.stopPropagation()
    setDraft(value)
    setEditing(true)
  }

  const finishCommit = async () => {
    const next = draft.trim()
    // Empty values reject (snap back). No toast, per the design language.
    if (!next || next === value) {
      setDraft(value)
      setEditing(false)
      return
    }
    setCommitting(true)
    try {
      await onCommit(next)
    } catch {
      // Parent is responsible for rollback / surfacing failure. We simply
      // exit edit mode quietly — the next `value` prop tick re-syncs `draft`.
    } finally {
      setCommitting(false)
      setEditing(false)
    }
  }

  const cancel = () => {
    setDraft(value)
    setEditing(false)
  }

  const handleKeyDown = (e: KeyboardEvent<HTMLInputElement>) => {
    if (e.key === 'Enter') {
      e.preventDefault()
      void finishCommit()
    } else if (e.key === 'Escape') {
      e.preventDefault()
      cancel()
    }
  }

  const typeStyles =
    variant === 'chrome'
      ? {
          fontSize: '0.9375rem', // ~15px
          fontWeight: 500,
          letterSpacing: '-0.005em',
          lineHeight: 1.3,
        }
      : {
          fontSize: '0.8125rem', // ~13px
          fontWeight: 500,
          letterSpacing: '-0.005em',
          lineHeight: 1.4,
        }

  // While committing, dip opacity slightly so the user can feel work happening
  // without breaking the calm of the surface.
  const opacity = committing ? 0.55 : 1

  if (editing) {
    return (
      <InputBase
        inputRef={inputRef}
        value={draft}
        onChange={(e) => setDraft(e.target.value)}
        onKeyDown={handleKeyDown}
        onBlur={() => void finishCommit()}
        onClick={(e) => e.stopPropagation()}
        disabled={committing}
        inputProps={{ 'aria-label': ariaLabel }}
        fullWidth
        sx={{
          ...typeStyles,
          color: tokens.textPrimary,
          px: 0.5,
          py: 0,
          borderRadius: 0.5,
          backgroundColor: tokens.editBg,
          opacity,
          transition: 'opacity 200ms ease',
          '& input': { p: 0 },
        }}
      />
    )
  }

  const displayText = value || emptyFallback || ''
  const isFallback = !value && !!emptyFallback

  const label = (
    <Typography
      component="span"
      onClick={beginEdit}
      sx={{
        ...typeStyles,
        color: isFallback ? 'rgba(0,0,0,0.36)' : tokens.textPrimary,
        cursor: disabled ? 'default' : 'text',
        opacity,
        transition: 'opacity 200ms ease, border-color 200ms ease',
        display: 'inline-block',
        maxWidth: '100%',
        whiteSpace: 'nowrap',
        overflow: 'hidden',
        textOverflow: 'ellipsis',
        // A barely-visible underline on hover signals editability without a
        // pencil icon.
        borderBottom: '1px dashed transparent',
        '&:hover': disabled
          ? undefined
          : { borderBottomColor: 'rgba(0,0,0,0.12)' },
      }}
    >
      {displayText}
    </Typography>
  )

  // Tooltip only when value exists (otherwise the fallback already explains).
  if (!disabled && value) {
    return (
      <Box sx={{ display: 'inline-flex', maxWidth: '100%', minWidth: 0 }}>
        <Tooltip title={value} placement="bottom-start" enterDelay={600}>
          {label}
        </Tooltip>
      </Box>
    )
  }

  return (
    <Box sx={{ display: 'inline-flex', maxWidth: '100%', minWidth: 0 }}>
      {label}
    </Box>
  )
}

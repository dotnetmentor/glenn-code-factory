import { useEffect, useMemo, useRef, useState, type KeyboardEvent } from 'react'
import { useSearchParams } from 'react-router-dom'
import {
  Box,
  CircularProgress,
  IconButton,
  InputBase,
  Popover,
  Stack,
  Tooltip,
  Typography,
} from '@mui/material'
import AddIcon from '@mui/icons-material/Add'
import ArchiveOutlinedIcon from '@mui/icons-material/ArchiveOutlined'
import DriveFileRenameOutlineIcon from '@mui/icons-material/DriveFileRenameOutline'
import { useQueryClient } from '@tanstack/react-query'
import { formatDistanceToNow, parseISO } from 'date-fns'
import {
  ConversationStatus,
  getGetApiConversationsIdQueryKey,
  getGetApiProjectsProjectIdConversationsQueryKey,
  useGetApiProjectsProjectIdConversations,
  usePostApiConversationsIdArchive,
  usePostApiConversationsIdRename,
  type ConversationSummary,
} from '../../../../../api/queries-commands'
import { useNotification } from '../../../../shared/contexts/NotificationContext'
import { clearLastBranchConversationId } from '../hooks/branchConversationMemory'

import { chromeTokens, semanticTokens, surfaceTokens } from '../../../shared/designTokens'

const tokens = { ...surfaceTokens, ...chromeTokens, ...semanticTokens }

/** Title length cap mirrored from the backend RenameConversation validator. */
const MAX_TITLE_LENGTH = 200

export interface ConversationPickerPopoverProps {
  open: boolean
  anchorEl: HTMLElement | null
  onClose: () => void
  projectId: string
  branchId: string
  activeConversationId: string | null
}

/**
 * Inline conversation picker that anchors off the {@code ChatChrome} title.
 *
 * <p>Replaces the sidebar's conversation list — the sidebar is now an agent /
 * project navigator. This popover stays scoped to the current branch and only
 * surfaces active conversations. Archived conversations are hidden entirely
 * in v1 (no "show archived" toggle).</p>
 *
 * <p>Each row carries hover-revealed rename and archive affordances.
 * Submitting Enter or blur calls the rename mutation; the popover does NOT
 * close while a rename is in flight so the user can chain operations. Per the
 * spec the row does not update visually until the request succeeds — we
 * invalidate both the list query and the affected conversation-by-id query
 * and let React Query refetch truth.</p>
 *
 * <p>Archive is one-click (no confirmation dialog in v1). The row stays
 * visible while the request is in flight; on success we invalidate the list
 * query and the row vanishes on the natural refetch — explicitly NO
 * optimistic removal per spec. On failure a toast surfaces and the row
 * remains. If the archived row was the active conversation, the {@code ?c=}
 * URL param is cleared so the chat canvas drops to the empty state.</p>
 */
export function ConversationPickerPopover({
  open,
  anchorEl,
  onClose,
  projectId,
  branchId,
  activeConversationId,
}: ConversationPickerPopoverProps) {
  const [, setSearchParams] = useSearchParams()
  const queryClient = useQueryClient()
  const { showError } = useNotification()

  const listQuery = useGetApiProjectsProjectIdConversations(
    projectId,
    { includeArchived: false },
    { query: { enabled: open && !!projectId } },
  )

  const conversations = useMemo<ConversationSummary[]>(() => {
    const list = listQuery.data ?? []
    return list
      .filter(
        (c) =>
          c.branchId === branchId && c.status === ConversationStatus.Active,
      )
      .slice()
      .sort((a, b) => {
        const ta = a.lastActivityAt ? Date.parse(a.lastActivityAt) : 0
        const tb = b.lastActivityAt ? Date.parse(b.lastActivityAt) : 0
        return tb - ta
      })
  }, [listQuery.data, branchId])

  // ── Per-row inline rename state (ephemeral, lives only here) ─────────────
  // Mirrors the {@code renamingId} pattern in {@link ConversationSidebar}, but
  // with an inline icon-button trigger (no overflow menu) per spec.
  const [renamingId, setRenamingId] = useState<string | null>(null)

  // Drop edit mode whenever the popover closes so a re-open doesn't reveal a
  // stale half-edited input.
  useEffect(() => {
    if (!open) setRenamingId(null)
  }, [open])

  const renameMutation = usePostApiConversationsIdRename()
  const archiveMutation = usePostApiConversationsIdArchive()

  const beginRename = (conversationId: string) => {
    setRenamingId(conversationId)
  }

  const cancelRename = () => {
    setRenamingId(null)
  }

  /**
   * Commit the rename. Returns true on success (so the row exits edit mode)
   * and false on validation/server failure (so the row stays in edit mode and
   * surfaces an inline error).
   *
   * <p>No optimistic update — per spec, "Picker doesn't update until the
   * request succeeds." We just invalidate on success and let React Query
   * refetch land the new title.</p>
   */
  const commitRename = (
    conversationId: string,
    nextRaw: string,
  ): Promise<boolean> => {
    const next = nextRaw.trim()
    // Validation gates handled inside the row component; we still defend in
    // depth here in case some future caller bypasses the input UI.
    if (next.length === 0 || next.length > MAX_TITLE_LENGTH) {
      return Promise.resolve(false)
    }
    return new Promise<boolean>((resolve) => {
      renameMutation.mutate(
        { id: conversationId, data: { title: next } },
        {
          onSuccess: () => {
            queryClient.invalidateQueries({
              queryKey: getGetApiProjectsProjectIdConversationsQueryKey(
                projectId,
                { includeArchived: false },
              ),
            })
            queryClient.invalidateQueries({
              queryKey: getGetApiConversationsIdQueryKey(conversationId),
            })
            resolve(true)
          },
          onError: () => {
            console.warn('[ConversationPickerPopover] rename failed')
            resolve(false)
          },
        },
      )
    })
  }

  /**
   * Archive a conversation from the picker. Per spec there is NO optimistic
   * removal — the row stays visible while the request is in flight; on
   * success we invalidate the list query and the row vanishes on the natural
   * refetch. On failure we surface a toast and the row remains. This is
   * intentionally different from {@link ConversationSidebar}, which mirrors
   * an optimistic-archive flow.
   *
   * <p>Active-conversation guard: if the archived row matches the current
   * {@code ?c=}, we also clear the URL param so the chat canvas drops to
   * the empty "new conversation" state.</p>
   *
   * <p>The popover stays open throughout — no {@code onClose()} call — so
   * the user can chain archive operations.</p>
   */
  const archiveConversation = (conversationId: string) => {
    archiveMutation.mutate(
      { id: conversationId },
      {
        onSuccess: () => {
          queryClient.invalidateQueries({
            queryKey: getGetApiProjectsProjectIdConversationsQueryKey(
              projectId,
              { includeArchived: false },
            ),
          })
          if (conversationId === activeConversationId) {
            clearLastBranchConversationId(branchId)
            setSearchParams(
              (prev) => {
                const next = new URLSearchParams(prev)
                next.delete('c')
                return next
              },
              { replace: false },
            )
          }
        },
        onError: () => {
          showError('Could not archive conversation')
        },
      },
    )
  }

  const onSelect = (conversationId: string) => {
    setSearchParams(
      (prev) => {
        const next = new URLSearchParams(prev)
        next.set('c', conversationId)
        return next
      },
      { replace: false },
    )
    onClose()
  }

  const onNewConversation = () => {
    clearLastBranchConversationId(branchId)
    setSearchParams(
      (prev) => {
        const next = new URLSearchParams(prev)
        next.delete('c')
        return next
      },
      { replace: false },
    )
    onClose()
  }

  return (
    <Popover
      open={open}
      anchorEl={anchorEl}
      onClose={onClose}
      anchorOrigin={{ vertical: 'bottom', horizontal: 'left' }}
      transformOrigin={{ vertical: 'top', horizontal: 'left' }}
      slotProps={{
        paper: {
          sx: {
            mt: 0.5,
            width: 320,
            maxHeight: 480,
            backgroundColor: tokens.paperBg,
            border: `1px solid ${tokens.hairline}`,
            boxShadow: '0 8px 24px rgba(0,0,0,0.08)',
            borderRadius: 1,
            overflow: 'hidden',
            display: 'flex',
            flexDirection: 'column',
          },
        },
      }}
    >
      {/* Top — new conversation affordance */}
      <Box
        role="button"
        tabIndex={0}
        onClick={onNewConversation}
        onKeyDown={(e) => {
          if (e.key === 'Enter' || e.key === ' ') {
            e.preventDefault()
            onNewConversation()
          }
        }}
        sx={{
          flexShrink: 0,
          px: 1.5,
          py: 1.25,
          display: 'flex',
          alignItems: 'center',
          gap: 1,
          cursor: 'pointer',
          borderBottom: `1px solid ${tokens.hairline}`,
          color: tokens.textPrimary,
          transition: 'background-color 120ms ease',
          '&:hover': { backgroundColor: tokens.rowHover },
          '&:focus-visible': {
            outline: `2px solid ${tokens.accent}`,
            outlineOffset: -2,
          },
        }}
      >
        <AddIcon sx={{ fontSize: 16, color: tokens.accent }} />
        <Typography
          sx={{
            fontSize: '0.8125rem',
            fontWeight: 500,
            letterSpacing: '-0.005em',
          }}
        >
          New conversation
        </Typography>
      </Box>

      {/* Scrollable list */}
      <Box
        sx={{
          flex: 1,
          minHeight: 0,
          overflowY: 'auto',
          '&::-webkit-scrollbar': { width: 6 },
          '&::-webkit-scrollbar-thumb': {
            backgroundColor: 'rgba(0,0,0,0.12)',
            borderRadius: 3,
          },
        }}
      >
        {listQuery.isLoading ? (
          <Stack alignItems="center" justifyContent="center" sx={{ py: 4 }}>
            <CircularProgress size={14} sx={{ color: tokens.textMuted }} />
          </Stack>
        ) : (
          <Box component="ul" sx={{ m: 0, p: 0, listStyle: 'none' }}>
            {conversations.map((c) => (
              <ConversationListItem
                key={c.id}
                conversation={c}
                active={c.id === activeConversationId}
                renaming={renamingId === c.id}
                onSelect={() => onSelect(c.id)}
                onBeginRename={() => beginRename(c.id)}
                onCancelRename={cancelRename}
                onCommitRename={(next) => commitRename(c.id, next)}
                onArchive={() => archiveConversation(c.id)}
              />
            ))}
          </Box>
        )}
      </Box>
    </Popover>
  )
}

interface ConversationListItemProps {
  conversation: ConversationSummary
  active: boolean
  renaming: boolean
  onSelect: () => void
  onBeginRename: () => void
  onCancelRename: () => void
  /** Resolves true on commit success, false on validation/server failure. */
  onCommitRename: (next: string) => Promise<boolean>
  /** Fire-and-forget archive request. Parent owns mutation + invalidation. */
  onArchive: () => void
}

function ConversationListItem({
  conversation,
  active,
  renaming,
  onSelect,
  onBeginRename,
  onCancelRename,
  onCommitRename,
  onArchive,
}: ConversationListItemProps) {
  const [hovered, setHovered] = useState(false)
  const relative = formatRelative(conversation.lastActivityAt)
  const title = conversation.title || 'Untitled conversation'

  return (
    <Box
      component="li"
      role={renaming ? undefined : 'button'}
      tabIndex={renaming ? -1 : 0}
      onMouseEnter={() => setHovered(true)}
      onMouseLeave={() => setHovered(false)}
      onClick={renaming ? undefined : onSelect}
      onKeyDown={(e) => {
        if (renaming) return
        if (e.key === 'Enter' || e.key === ' ') {
          e.preventDefault()
          onSelect()
        }
      }}
      sx={{
        position: 'relative',
        px: 1.5,
        py: 1,
        pr: 7, // reserve room for the trailing rename + archive icon buttons
        cursor: renaming ? 'text' : 'pointer',
        backgroundColor: active ? tokens.rowActive : 'transparent',
        transition: 'background-color 120ms ease',
        '&:hover': {
          backgroundColor: active ? tokens.rowActive : tokens.rowHover,
        },
        '&:focus-visible': {
          outline: `2px solid ${tokens.accent}`,
          outlineOffset: -2,
        },
      }}
    >
      {active && (
        <Box
          aria-hidden
          sx={{
            position: 'absolute',
            left: 0,
            top: 0,
            bottom: 0,
            width: 2,
            backgroundColor: tokens.accent,
          }}
        />
      )}

      {renaming ? (
        <InlineRenameEditor
          initialValue={conversation.title ?? ''}
          onCancel={onCancelRename}
          onCommit={onCommitRename}
        />
      ) : (
        <Typography
          sx={{
            fontSize: '0.8125rem',
            fontWeight: active ? 600 : 500,
            color: tokens.textPrimary,
            whiteSpace: 'nowrap',
            overflow: 'hidden',
            textOverflow: 'ellipsis',
            letterSpacing: '-0.005em',
          }}
          title={relative ? `${title} — ${relative}` : title}
        >
          {title}
          {relative && (
            <Box
              component="span"
              sx={{ color: tokens.textMuted, fontWeight: 400 }}
            >
              {' — '}
              {relative}
            </Box>
          )}
        </Typography>
      )}

      {/* Right-edge row actions — appear on hover (and while the row is the
          active selection so the user gets a stable affordance for the
          currently-open conversation). Hidden while the row itself is in
          edit mode to keep the input clean.

          Order, left → right: rename, archive. The archive button is a
          one-click action per the v1 spec (no confirmation dialog). */}
      {!renaming && (
        <Box
          sx={{
            position: 'absolute',
            top: 0,
            bottom: 0,
            right: 4,
            display: 'flex',
            alignItems: 'center',
            gap: 0.25,
            opacity: hovered || active ? 1 : 0,
            transition: 'opacity 120ms ease',
          }}
        >
          <Tooltip title="Rename" placement="left" enterDelay={400}>
            <IconButton
              size="small"
              aria-label="Rename conversation"
              onClick={(e) => {
                // Don't let the row's click handler swallow this and switch
                // the active conversation.
                e.stopPropagation()
                onBeginRename()
              }}
              sx={{
                p: 0.25,
                color: tokens.textMuted,
                '&:hover': {
                  color: tokens.textPrimary,
                  backgroundColor: 'transparent',
                },
              }}
            >
              <DriveFileRenameOutlineIcon sx={{ fontSize: 16 }} />
            </IconButton>
          </Tooltip>
          <Tooltip title="Archive" placement="left" enterDelay={400}>
            <IconButton
              size="small"
              aria-label="Archive conversation"
              onClick={(e) => {
                // Don't let the row's click handler swallow this and switch
                // the active conversation. Also keep the popover open — the
                // parent never calls onClose() for archive operations.
                e.stopPropagation()
                onArchive()
              }}
              sx={{
                p: 0.25,
                color: tokens.textMuted,
                '&:hover': {
                  color: tokens.textPrimary,
                  backgroundColor: 'transparent',
                },
              }}
            >
              <ArchiveOutlinedIcon sx={{ fontSize: 16 }} />
            </IconButton>
          </Tooltip>
        </Box>
      )}
    </Box>
  )
}

interface InlineRenameEditorProps {
  initialValue: string
  onCancel: () => void
  /** Resolves true on commit success, false on validation/server failure. */
  onCommit: (next: string) => Promise<boolean>
}

/**
 * Inline rename input for a picker row.
 *
 * <p>Mirrors the {@link EditableTitle} sidebar variant visually (13px / 500
 * weight, calm edit-bg, opacity dip while committing) but adds explicit
 * inline validation messages for empty and overlong titles. We do not call
 * the rename mutation on invalid input — empty / >200 chars surface the
 * error and keep the row in edit mode so the user can correct without
 * losing their draft.</p>
 *
 * <p>Submission semantics:
 * <ul>
 *   <li>Enter — validate; commit if valid.</li>
 *   <li>Esc — drop edit mode, no mutation.</li>
 *   <li>Blur — same as Enter (validate + commit).</li>
 * </ul></p>
 */
function InlineRenameEditor({
  initialValue,
  onCancel,
  onCommit,
}: InlineRenameEditorProps) {
  const [draft, setDraft] = useState(initialValue)
  const [error, setError] = useState<string | null>(null)
  const [committing, setCommitting] = useState(false)
  const inputRef = useRef<HTMLInputElement | null>(null)
  // Guards against the blur handler firing after a successful Enter commit
  // (the row unmounts the editor when commit succeeds, but a queued blur can
  // still re-enter finishCommit if we don't latch it).
  const finishedRef = useRef(false)

  useEffect(() => {
    inputRef.current?.focus()
    inputRef.current?.select()
  }, [])

  const validate = (value: string): string | null => {
    const trimmed = value.trim()
    if (trimmed.length === 0) return 'Title cannot be empty.'
    if (trimmed.length > MAX_TITLE_LENGTH) {
      return `Title must be ${MAX_TITLE_LENGTH} characters or fewer.`
    }
    return null
  }

  const finishCommit = async () => {
    if (finishedRef.current) return
    const validation = validate(draft)
    if (validation) {
      setError(validation)
      // Do NOT call the mutation. Stay in edit mode so the user can fix it.
      // Re-focus so they can keep typing.
      inputRef.current?.focus()
      return
    }
    if (draft.trim() === initialValue.trim()) {
      // No change — exit quietly.
      finishedRef.current = true
      onCancel()
      return
    }
    setError(null)
    setCommitting(true)
    const ok = await onCommit(draft)
    setCommitting(false)
    if (ok) {
      finishedRef.current = true
      // Editor will be unmounted by the parent flipping renamingId to null.
      onCancel()
    } else {
      // Mutation failed; keep the editor open so the user can retry.
      setError('Could not rename. Try again.')
      inputRef.current?.focus()
    }
  }

  const handleKeyDown = (e: KeyboardEvent<HTMLInputElement>) => {
    if (e.key === 'Enter') {
      e.preventDefault()
      void finishCommit()
    } else if (e.key === 'Escape') {
      e.preventDefault()
      finishedRef.current = true
      onCancel()
    }
  }

  return (
    <Box>
      <InputBase
        inputRef={inputRef}
        value={draft}
        onChange={(e) => {
          setDraft(e.target.value)
          if (error) setError(null)
        }}
        onKeyDown={handleKeyDown}
        onBlur={() => void finishCommit()}
        onClick={(e) => e.stopPropagation()}
        disabled={committing}
        inputProps={{
          'aria-label': 'Conversation title',
          'aria-invalid': error ? true : undefined,
          maxLength: MAX_TITLE_LENGTH + 50, // soft cap; we validate exactly
        }}
        fullWidth
        sx={{
          fontSize: '0.8125rem',
          fontWeight: 500,
          letterSpacing: '-0.005em',
          lineHeight: 1.4,
          color: tokens.textPrimary,
          px: 0.5,
          py: 0,
          borderRadius: 0.5,
          backgroundColor: tokens.editBg,
          opacity: committing ? 0.55 : 1,
          transition: 'opacity 200ms ease',
          border: error ? `1px solid ${tokens.errorText}` : '1px solid transparent',
          '& input': { p: 0 },
        }}
      />
      {error && (
        <Typography
          role="alert"
          sx={{
            mt: 0.25,
            fontSize: '0.6875rem',
            color: tokens.errorText,
            letterSpacing: '-0.005em',
          }}
        >
          {error}
        </Typography>
      )}
    </Box>
  )
}

function formatRelative(iso: string | null | undefined): string {
  if (!iso) return ''
  try {
    const parsed = parseISO(iso)
    if (Number.isNaN(parsed.getTime())) return ''
    return formatDistanceToNow(parsed, { addSuffix: true })
  } catch {
    return ''
  }
}

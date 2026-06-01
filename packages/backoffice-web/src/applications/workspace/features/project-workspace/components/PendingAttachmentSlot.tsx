import { useEffect } from 'react'
import { useAttachmentUpload, type AttachmentState } from '../hooks/useAttachmentUpload'
import { AttachmentChip } from './AttachmentChip'

/**
 * Snapshot of a slot's upload progress that the parent {@link ChatPanel}
 * consumes for Send-gating and the helper-text line below the input row.
 *
 * <p>Kept intentionally minimal — only the bits the parent derives composer
 * state from. Progress + error stay inside the chip; they don't influence
 * the parent's behaviour.</p>
 */
export interface SlotStateSnapshot {
  state: AttachmentState
  attachmentId: string | null
}

export interface PendingAttachmentSlotProps {
  /**
   * Stable frontend-only key the parent uses to identify this slot in its
   * `pendingAttachments` array. Distinct from the backend `attachmentId`,
   * which only arrives after the presign call resolves.
   */
  slotId: string
  file: File
  conversationId: string
  branchId: string
  /**
   * Fires on every state/attachmentId transition (and once on mount, after
   * the first effect tick) so the parent can rebuild its aggregate snapshot
   * without subscribing to the upload hook itself.
   */
  onStateChange: (slotId: string, snapshot: SlotStateSnapshot) => void
  /** Parent removes the slot from its array, which unmounts us. */
  onRemove: (slotId: string) => void
}

/**
 * Lives one-per-pending-attachment inside the composer. The wrapper exists
 * so we can call {@link useAttachmentUpload} (which has its own SignalR
 * subscription + XHR lifecycle) per file without polluting the parent
 * panel's render with N hook calls.
 *
 * <p>Communication upward is via the `onStateChange` callback. The parent
 * keeps a {@code Record<slotId, SlotStateSnapshot>} and derives Send-button
 * gating + helper-text from the aggregate. We chose the callback pattern
 * over a shared context because (a) the slot tree is shallow (one level),
 * (b) there's no other consumer that needs the data, and (c) it keeps each
 * slot's own re-renders local — the parent only re-renders when an
 * individual slot's snapshot actually changes.</p>
 */
export function PendingAttachmentSlot(props: PendingAttachmentSlotProps) {
  const { slotId, file, conversationId, branchId, onStateChange, onRemove } =
    props

  const upload = useAttachmentUpload({ file, conversationId, branchId })

  // Push every state/attachmentId transition upward. We intentionally do NOT
  // include onStateChange in the dependency list — the parent's callback
  // identity changes on every render (it closes over setState) and the
  // payload is fully captured by state + attachmentId, so the extra deps
  // would just cause spurious re-fires.
  useEffect(() => {
    onStateChange(slotId, {
      state: upload.state,
      attachmentId: upload.attachmentId,
    })
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [slotId, upload.state, upload.attachmentId])

  return (
    <AttachmentChip
      state={upload.state}
      fileName={upload.fileName}
      sizeBytes={upload.sizeBytes}
      progress={upload.progress}
      error={upload.error}
      onRemove={() => onRemove(slotId)}
      onRetry={
        upload.state === 'uploadFailed' || upload.state === 'stagingFailed'
          ? upload.retry
          : undefined
      }
    />
  )
}

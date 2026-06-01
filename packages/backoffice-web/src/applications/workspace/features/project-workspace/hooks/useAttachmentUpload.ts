import { useCallback, useEffect, useReducer, useRef } from 'react'
import {
  postApiAttachmentsPresign,
  postApiAttachmentsIdComplete,
} from '@/api/queries-commands'
import { useAgentHub } from '@/lib/signalr'

/**
 * Per-attachment lifecycle state machine.
 *
 * <p>Mirrors the UX states enumerated in the {@code chat-file-attachments}
 * spec. The chip component switches on this value to render the right
 * visual + affordances. There is no "removed" state here — when the parent
 * decides to remove the attachment it simply unmounts the chip and the hook
 * unsubscribes / aborts in cleanup.</p>
 */
export type AttachmentState =
  | 'queued'
  | 'uploading'
  | 'staging'
  | 'ready'
  | 'uploadFailed'
  | 'stagingFailed'
  | 'rejected'

/** Max upload size — matches {@code Attachment.MaxSizeBytes} on the backend (50 MiB). */
const MAX_SIZE_BYTES = 52_428_800

/** Soft timeout we wait for the daemon's SignalR Ready/Failed before giving up. */
const STAGING_TIMEOUT_MS = 30_000

/**
 * Reducer state — all derived UI state in one place so progress updates and
 * lifecycle transitions can't race each other. The hook's public return shape
 * is a thin projection of this.
 */
interface State {
  attachmentId: string | null
  state: AttachmentState
  progress: number
  error: string | null
  /**
   * Bumps every time {@link useAttachmentUpload.retry} is called. The async
   * flow checks this against the value it captured at start and bails if
   * they've diverged — guards against a stale presign/PUT callback mutating
   * state after the user already retried.
   */
  attemptKey: number
}

type Action =
  | { kind: 'reset'; attemptKey: number }
  | { kind: 'rejected'; error: string }
  | { kind: 'presignSucceeded'; attachmentId: string }
  | { kind: 'progress'; progress: number }
  | { kind: 'uploadFailed'; error: string }
  | { kind: 'completeStarted' }
  | { kind: 'staging' }
  | { kind: 'ready' }
  | { kind: 'stagingFailed'; error: string }

function reducer(state: State, action: Action): State {
  switch (action.kind) {
    case 'reset':
      return {
        attachmentId: null,
        state: 'queued',
        progress: 0,
        error: null,
        attemptKey: action.attemptKey,
      }
    case 'rejected':
      return { ...state, state: 'rejected', error: action.error, progress: 0 }
    case 'presignSucceeded':
      return {
        ...state,
        attachmentId: action.attachmentId,
        state: 'uploading',
        progress: 0,
        error: null,
      }
    case 'progress':
      // Only meaningful during 'uploading' — ignore late events that arrive
      // after we've already moved to 'staging' or beyond.
      if (state.state !== 'uploading') return state
      return { ...state, progress: action.progress }
    case 'uploadFailed':
      return { ...state, state: 'uploadFailed', error: action.error }
    case 'completeStarted':
      // Visual stays in "uploading" at 100% while the complete round-trip
      // is in flight. Keeps the bar pinned full instead of flashing back
      // to 0 before we flip to 'staging'.
      return { ...state, progress: 100 }
    case 'staging':
      return { ...state, state: 'staging', progress: 100, error: null }
    case 'ready':
      return { ...state, state: 'ready', error: null }
    case 'stagingFailed':
      return { ...state, state: 'stagingFailed', error: action.error }
    default:
      return state
  }
}

/** Public return shape of {@link useAttachmentUpload}. */
export interface AttachmentUploadHandle {
  /** Null until presign succeeds; afterwards the server-issued attachment guid. */
  attachmentId: string | null
  fileName: string
  sizeBytes: number
  state: AttachmentState
  /** 0–100. Only meaningful when {@link state} === 'uploading'. */
  progress: number
  /** Human-readable error for the current failure state (or null). */
  error: string | null
  /**
   * Restart the full flow from validate → presign → PUT → complete. Safe to
   * call from any failure state; idempotent enough that a double-click won't
   * fire two parallel uploads (the attemptKey guard short-circuits the
   * stale one).
   */
  retry: () => void
  /**
   * Abort any in-flight upload. The parent component should call this in the
   * same effect that unmounts the chip (e.g. when the user clicks the X).
   * The hook itself does NOT transition into a "removed" state — the parent
   * owns removal.
   */
  cancel: () => void
}

export interface UseAttachmentUploadParams {
  file: File
  /** Guid of the chat conversation; scopes the attachment server-side. */
  conversationId: string
  /** Guid of the branch; needed to filter SignalR pushes to this chat panel. */
  branchId: string
}

/**
 * Manage the full client-side lifecycle of a single chat attachment.
 *
 * <p>Owns: client-side validation, presign call, XHR PUT with progress
 * events, complete call, SignalR subscription for the daemon staging
 * acknowledgement, the staging-timeout safety net, and the cancel/retry
 * affordances.</p>
 *
 * <p>One instance per file: the parent composer keeps an array of
 * {@code { id, file }} records and mounts a wrapper component per record;
 * the wrapper calls this hook and renders an {@code AttachmentChip}. When
 * the user clicks X, the parent removes the record, React unmounts the
 * wrapper, and this hook's cleanup aborts the XHR plus detaches the SignalR
 * listener.</p>
 */
export function useAttachmentUpload(
  params: UseAttachmentUploadParams,
): AttachmentUploadHandle {
  const { file, conversationId, branchId } = params

  const [state, dispatch] = useReducer(reducer, undefined, () => ({
    attachmentId: null,
    state: 'queued' as AttachmentState,
    progress: 0,
    error: null,
    attemptKey: 0,
  }))

  // ── refs (mutable across renders, not part of reactive state) ─────────────
  const xhrRef = useRef<XMLHttpRequest | null>(null)
  const stagingTimerRef = useRef<ReturnType<typeof setTimeout> | null>(null)
  /**
   * Most recent attemptKey we've started a flow for. Async callbacks compare
   * against this on completion and bail if the user has retried in the
   * meantime — prevents a stale PUT's onload from flipping a freshly-restarted
   * flow into 'staging'.
   */
  const activeAttemptRef = useRef<number>(0)
  /**
   * Set to true the moment the parent unmounts us OR cancel() is called. We
   * never dispatch after this flips — guards against unmount races.
   */
  const cancelledRef = useRef<boolean>(false)

  // We need the AgentHub connection for the daemon staging-ack listener. The
  // hub is ref-counted, so multiple useAttachmentUpload instances on the
  // same branch share the same socket as the parent ChatPanel.
  const { connection } = useAgentHub({ branchId })

  // ── public retry/cancel ──────────────────────────────────────────────────
  const retry = useCallback(() => {
    if (cancelledRef.current) return
    // Tear down anything in flight from the previous attempt.
    const xhr = xhrRef.current
    if (xhr) {
      xhrRef.current = null
      try {
        xhr.abort()
      } catch {
        // best-effort
      }
    }
    if (stagingTimerRef.current !== null) {
      clearTimeout(stagingTimerRef.current)
      stagingTimerRef.current = null
    }
    const nextAttempt = activeAttemptRef.current + 1
    activeAttemptRef.current = nextAttempt
    dispatch({ kind: 'reset', attemptKey: nextAttempt })
    void runFullFlow({
      attemptKey: nextAttempt,
      file,
      conversationId,
      dispatch,
      xhrRef,
      activeAttemptRef,
      cancelledRef,
    })
  }, [file, conversationId])

  const cancel = useCallback(() => {
    cancelledRef.current = true
    const xhr = xhrRef.current
    if (xhr) {
      xhrRef.current = null
      try {
        xhr.abort()
      } catch {
        // best-effort
      }
    }
    if (stagingTimerRef.current !== null) {
      clearTimeout(stagingTimerRef.current)
      stagingTimerRef.current = null
    }
  }, [])

  // ── start the flow on mount (and whenever the file identity changes) ─────
  // StrictMode safety: in dev, React 19 mounts → unmounts → remounts every
  // effect synchronously. If we kicked off runFullFlow inline, we'd presign +
  // PUT + /complete TWICE per file (two DB rows, two R2 objects). Defer the
  // start by a macrotask: the cleanup from the throw-away mount fires
  // synchronously and clears the timer before it runs. Only the surviving
  // mount's timer actually fires runFullFlow.
  useEffect(() => {
    cancelledRef.current = false
    const attempt = activeAttemptRef.current
    const startTimer = setTimeout(() => {
      if (cancelledRef.current) return
      void runFullFlow({
        attemptKey: attempt,
        file,
        conversationId,
        dispatch,
        xhrRef,
        activeAttemptRef,
        cancelledRef,
      })
    }, 0)
    return () => {
      // Parent is unmounting — user removed the chip, ChatPanel tore down,
      // branch swap, OR React StrictMode dev double-mount. Stop everything.
      clearTimeout(startTimer)
      cancelledRef.current = true
      const xhr = xhrRef.current
      if (xhr) {
        xhrRef.current = null
        try {
          xhr.abort()
        } catch {
          // best-effort
        }
      }
      if (stagingTimerRef.current !== null) {
        clearTimeout(stagingTimerRef.current)
        stagingTimerRef.current = null
      }
    }
    // Mount-once-per-file semantics: the parent never swaps file or
    // conversationId on a mounted instance, but if it did we'd treat it as
    // a new attachment and restart.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [file, conversationId])

  // ── SignalR subscription for the daemon staging acknowledgement ──────────
  useEffect(() => {
    if (!connection) return
    if (!state.attachmentId) return
    // Capture the id we're filtering on — the listener fires for ALL chips
    // on the branch and we only want our own.
    const myId = state.attachmentId
    const myConvId = conversationId

    const unsubscribe = connection.onAttachmentStateChanged((payload) => {
      if (payload.attachmentId !== myId) return
      if (payload.conversationId !== myConvId) return
      if (cancelledRef.current) return

      if (payload.state === 'Ready') {
        if (stagingTimerRef.current !== null) {
          clearTimeout(stagingTimerRef.current)
          stagingTimerRef.current = null
        }
        dispatch({ kind: 'ready' })
      } else if (payload.state === 'Failed') {
        if (stagingTimerRef.current !== null) {
          clearTimeout(stagingTimerRef.current)
          stagingTimerRef.current = null
        }
        dispatch({
          kind: 'stagingFailed',
          error: payload.error ?? "Couldn't deliver to agent runtime",
        })
      }
    })
    return () => {
      unsubscribe()
    }
  }, [connection, state.attachmentId, conversationId])

  // ── staging timeout safety net ───────────────────────────────────────────
  // Per spec edge case "Upload finishes but runtime is offline": if we've
  // been in 'staging' for too long with no SignalR push, transition to
  // stagingFailed so the user gets Retry+Remove instead of an indefinite
  // spinner.
  useEffect(() => {
    if (state.state !== 'staging') {
      if (stagingTimerRef.current !== null) {
        clearTimeout(stagingTimerRef.current)
        stagingTimerRef.current = null
      }
      return
    }
    if (stagingTimerRef.current !== null) {
      clearTimeout(stagingTimerRef.current)
    }
    stagingTimerRef.current = setTimeout(() => {
      if (cancelledRef.current) return
      dispatch({
        kind: 'stagingFailed',
        error: "Couldn't deliver to agent runtime",
      })
    }, STAGING_TIMEOUT_MS)
    return () => {
      if (stagingTimerRef.current !== null) {
        clearTimeout(stagingTimerRef.current)
        stagingTimerRef.current = null
      }
    }
  }, [state.state])

  return {
    attachmentId: state.attachmentId,
    fileName: file.name,
    sizeBytes: file.size,
    state: state.state,
    progress: state.progress,
    error: state.error,
    retry,
    cancel,
  }
}

/**
 * The canonical async flow: validate → presign → PUT → complete. Kept as a
 * free function (rather than a closure) so the mount effect and retry()
 * share the exact same code path. All branching state lives in the local
 * {@code putOk} flag so we never need to read stale reducer state mid-flight.
 */
async function runFullFlow(args: {
  attemptKey: number
  file: File
  conversationId: string
  dispatch: React.Dispatch<Action>
  xhrRef: React.MutableRefObject<XMLHttpRequest | null>
  activeAttemptRef: React.MutableRefObject<number>
  cancelledRef: React.MutableRefObject<boolean>
}): Promise<void> {
  const {
    attemptKey,
    file,
    conversationId,
    dispatch,
    xhrRef,
    activeAttemptRef,
    cancelledRef,
  } = args

  const stillCurrent = () =>
    !cancelledRef.current && activeAttemptRef.current === attemptKey

  // 0. Client-side validation (mirrors backend exactly so tampered clients
  // get the same answer twice).
  if (file.size <= 0) {
    if (stillCurrent()) dispatch({ kind: 'rejected', error: 'Empty file' })
    return
  }
  if (file.size > MAX_SIZE_BYTES) {
    const mb = (file.size / 1024 / 1024).toFixed(1)
    if (stillCurrent())
      dispatch({
        kind: 'rejected',
        error: `File too large (${mb} MB). Max is 50 MB.`,
      })
    return
  }

  // 1. Presign — backend mints the attachment row and hands back a short-
  // lived PUT URL signed for our exact (key, contentType).
  let presignResult: Awaited<ReturnType<typeof postApiAttachmentsPresign>>
  try {
    presignResult = await postApiAttachmentsPresign({
      conversationId,
      fileName: file.name,
      contentType: file.type || null,
      sizeBytes: file.size,
    })
  } catch (err) {
    if (stillCurrent())
      dispatch({
        kind: 'uploadFailed',
        error: errorMessage(err, 'Could not start upload.'),
      })
    return
  }
  if (!stillCurrent()) return
  const { attachmentId, uploadUrl } = presignResult
  dispatch({ kind: 'presignSucceeded', attachmentId })

  // 2. Direct browser → storage PUT. XHR (not fetch) so we can surface real
  // upload progress on the chip; fetch's request body has no progress API.
  const putOk = await new Promise<boolean>((resolve) => {
    const xhr = new XMLHttpRequest()
    xhrRef.current = xhr

    xhr.open('PUT', uploadUrl)
    // The Content-Type MUST match what the backend signed in the presign
    // call (the SDK includes contentType in the signed headers when we
    // pass one). Mismatch → SignatureDoesNotMatch.
    xhr.setRequestHeader(
      'Content-Type',
      file.type || 'application/octet-stream',
    )

    xhr.upload.onprogress = (e) => {
      if (!e.lengthComputable) return
      if (!stillCurrent()) return
      dispatch({
        kind: 'progress',
        progress: Math.round((e.loaded / e.total) * 100),
      })
    }
    xhr.onload = () => {
      xhrRef.current = null
      if (!stillCurrent()) return resolve(false)
      if (xhr.status >= 200 && xhr.status < 300) {
        dispatch({ kind: 'completeStarted' })
        resolve(true)
      } else {
        dispatch({
          kind: 'uploadFailed',
          error: `Upload failed (HTTP ${xhr.status}).`,
        })
        resolve(false)
      }
    }
    xhr.onerror = () => {
      xhrRef.current = null
      if (!stillCurrent()) return resolve(false)
      dispatch({
        kind: 'uploadFailed',
        error: 'Network error while uploading.',
      })
      resolve(false)
    }
    xhr.onabort = () => {
      // Parent cancelled or we retried — no state transition; the caller
      // (cancel/retry/unmount) already owns the next step.
      xhrRef.current = null
      resolve(false)
    }
    xhr.send(file)
  })

  if (!putOk) return
  if (!stillCurrent()) return

  // 3. Tell the backend the bytes are up — backend then kicks the daemon to
  // stage. The daemon's SignalR ack is what flips us to 'ready'; here we
  // just promote the chip to "staging".
  try {
    await postApiAttachmentsIdComplete(attachmentId)
  } catch (err) {
    if (stillCurrent())
      dispatch({
        kind: 'uploadFailed',
        error: errorMessage(err, 'Could not finalise upload.'),
      })
    return
  }
  if (!stillCurrent()) return
  dispatch({ kind: 'staging' })
}

/** Pull a usable message out of any thrown value. */
function errorMessage(err: unknown, fallback: string): string {
  if (err instanceof Error) return err.message || fallback
  if (typeof err === 'string') return err
  if (err && typeof err === 'object') {
    const maybe = err as {
      message?: unknown
      detail?: unknown
      title?: unknown
    }
    if (typeof maybe.message === 'string') return maybe.message
    if (typeof maybe.detail === 'string') return maybe.detail
    if (typeof maybe.title === 'string') return maybe.title
  }
  return fallback
}

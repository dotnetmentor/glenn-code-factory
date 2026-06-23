import { useCallback, useEffect, useState } from 'react'

/**
 * Per-conversation agent backend selectors. <c>cursor</c> is the historical
 * default and keeps today's UX byte-for-byte identical; <c>claude</c> opts the
 * conversation into the Claude Agent SDK backend (model + reasoning switching).
 */
export type AgentBackend = 'cursor' | 'claude'

/**
 * SDK reasoning effort levels surfaced by the composer's reasoning dropdown.
 * Only meaningful on the Claude backend with a reasoning-capable model; the
 * Cursor path ignores it entirely.
 */
export type ReasoningEffort = 'low' | 'medium' | 'high' | 'xhigh' | 'max'

/**
 * The full per-conversation agent override record. Stored as one JSON blob per
 * conversation so the backend choice, the selected model (interpretation
 * depends on {@link AgentOverride.backend}), and the reasoning effort travel
 * together and stay internally consistent.
 *
 * <p><b>Model semantics by backend:</b> when {@code backend === 'cursor'} the
 * {@code model} field holds a Cursor model id (or {@code null} = project
 * default); when {@code backend === 'claude'} it holds a Claude model id (or
 * {@code null} = the catalog's system default).</p>
 */
export type AgentOverride = {
  backend: AgentBackend
  /**
   * Model id for the active backend, or {@code null} to fall back to the
   * project/catalog default. Cursor-model id when {@code backend === 'cursor'},
   * Claude-model id when {@code backend === 'claude'}.
   */
  model: string | null
  /**
   * Reasoning effort for the Claude backend. {@code null} = use the model's
   * {@code defaultEffort}. Ignored on the Cursor path.
   */
  reasoningEffort: ReasoningEffort | null
}

/** The implicit override every conversation starts from: Cursor, project default. */
export const DEFAULT_AGENT_OVERRIDE: AgentOverride = {
  backend: 'cursor',
  model: null,
  reasoningEffort: null,
}

/**
 * localStorage key under which a per-conversation agent override is persisted.
 * The override is sticky for the lifetime of the conversation: the user can
 * flip the dropdown in the composer once and every subsequent send for that
 * conversation carries the chosen backend/model/effort through the
 * {@code submitPrompt} payload until they change it again or clear it.
 *
 * <p>The override is intentionally per-conversation rather than per-session
 * or per-branch — switching branches creates a new conversation, and a
 * conversation is the unit a user reads as "my chat" mentally. Storing it
 * under the conversation id means closing the tab and coming back keeps the
 * choice; clearing localStorage resets it to the project default.</p>
 *
 * <p>This is the v2 key (a JSON record). The original v1 key stored a bare
 * model-id string under {@link legacyAgentModelOverrideKey}; we still read that
 * on first load so existing users aren't disrupted, then write forward under
 * the v2 key.</p>
 */
export function agentOverrideKey(conversationId: string): string {
  return `agent-override:${conversationId}`
}

/**
 * The original (v1) localStorage key — a bare Cursor-model-id string. Read for
 * backward compatibility; never written to anymore.
 */
export function legacyAgentModelOverrideKey(conversationId: string): string {
  return `agent-model-override:${conversationId}`
}

/**
 * Back-compat alias for {@link legacyAgentModelOverrideKey}. Kept so any
 * external import of the old name keeps compiling.
 * @deprecated Use {@link agentOverrideKey} for the v2 record key.
 */
export const agentModelOverrideKey = legacyAgentModelOverrideKey

function coerceBackend(raw: unknown): AgentBackend {
  return raw === 'claude' ? 'claude' : 'cursor'
}

function coerceEffort(raw: unknown): ReasoningEffort | null {
  return raw === 'low' ||
    raw === 'medium' ||
    raw === 'high' ||
    raw === 'xhigh' ||
    raw === 'max'
    ? raw
    : null
}

function parseStoredOverride(raw: string | null): AgentOverride | null {
  if (!raw) return null
  const trimmed = raw.trim()
  if (trimmed.length === 0) return null
  // v2 record — a JSON object.
  if (trimmed.startsWith('{')) {
    try {
      const obj = JSON.parse(trimmed) as Record<string, unknown>
      const model =
        typeof obj.model === 'string' && obj.model.trim().length > 0
          ? obj.model
          : null
      return {
        backend: coerceBackend(obj.backend),
        model,
        reasoningEffort: coerceEffort(obj.reasoningEffort),
      }
    } catch {
      return null
    }
  }
  // v1 record — a bare Cursor model-id string. Promote it to a cursor override.
  return { backend: 'cursor', model: trimmed, reasoningEffort: null }
}

/**
 * Read the full per-conversation override (or {@code null} when none is set).
 * Reads the v2 JSON record first, then falls back to the v1 bare-string key so
 * conversations created before this change keep their Cursor model choice.
 *
 * <p>Used by callers (such as the chat send path) that only need the current
 * value at submit time and don't want to subscribe to localStorage events.
 * Falls back to {@code null} when the keys are missing or the
 * {@code localStorage} API is unavailable (private mode quirks, SSR builds).</p>
 */
export function readAgentOverride(
  conversationId: string | null | undefined,
): AgentOverride | null {
  if (!conversationId) return null
  if (typeof window === 'undefined') return null
  try {
    const v2 = window.localStorage.getItem(agentOverrideKey(conversationId))
    const parsed = parseStoredOverride(v2)
    if (parsed) return parsed
    // Fall back to the legacy bare-string key.
    const v1 = window.localStorage.getItem(
      legacyAgentModelOverrideKey(conversationId),
    )
    return parseStoredOverride(v1)
  } catch {
    return null
  }
}

/**
 * Back-compat reader for the v1 callers that only want the Cursor model id.
 * Returns the model id when the conversation is on the Cursor backend, else
 * {@code null}. Kept so the previous {@code readAgentModelOverride} import
 * keeps working without behaviour change for cursor conversations.
 *
 * @deprecated Prefer {@link readAgentOverride} which carries backend + effort.
 */
export function readAgentModelOverride(
  conversationId: string | null | undefined,
): string | null {
  const ov = readAgentOverride(conversationId)
  if (!ov) return null
  if (ov.backend !== 'cursor') return null
  return ov.model
}

/**
 * Reactive hook for the per-conversation override. Returns the current value
 * (defaulting to {@link DEFAULT_AGENT_OVERRIDE} when none is set) plus a setter
 * that merges a partial patch and a clear that resets to the project default.
 *
 * <p>Listens for cross-tab updates via the native {@code storage} event so two
 * tabs against the same conversation stay in sync. Same-tab writes go through
 * the setter and update React state directly — the {@code storage} event
 * doesn't fire on the tab that wrote, which is why we update both.</p>
 */
export function useAgentOverride(conversationId: string | null) {
  const [value, setValue] = useState<AgentOverride>(
    () => readAgentOverride(conversationId) ?? DEFAULT_AGENT_OVERRIDE,
  )
  // Whether anything has been explicitly chosen (vs the implicit default).
  const [hasOverride, setHasOverride] = useState<boolean>(
    () => readAgentOverride(conversationId) !== null,
  )

  // Re-read whenever the conversation id flips — the parent route's URL change
  // means a different conversation is now in focus.
  useEffect(() => {
    const stored = readAgentOverride(conversationId)
    setValue(stored ?? DEFAULT_AGENT_OVERRIDE)
    setHasOverride(stored !== null)
  }, [conversationId])

  // Cross-tab sync — listen for native storage events on either key. Same-tab
  // writes are updated synchronously below in {@code patch} / {@code clear}.
  useEffect(() => {
    if (!conversationId) return
    if (typeof window === 'undefined') return
    const v2Key = agentOverrideKey(conversationId)
    const v1Key = legacyAgentModelOverrideKey(conversationId)
    const handler = (e: StorageEvent) => {
      if (e.key !== v2Key && e.key !== v1Key) return
      const stored = readAgentOverride(conversationId)
      setValue(stored ?? DEFAULT_AGENT_OVERRIDE)
      setHasOverride(stored !== null)
    }
    window.addEventListener('storage', handler)
    return () => window.removeEventListener('storage', handler)
  }, [conversationId])

  const write = useCallback(
    (next: AgentOverride) => {
      if (!conversationId) return
      if (typeof window === 'undefined') {
        setValue(next)
        setHasOverride(true)
        return
      }
      try {
        window.localStorage.setItem(
          agentOverrideKey(conversationId),
          JSON.stringify(next),
        )
        // Drop the stale v1 key so the two never disagree.
        window.localStorage.removeItem(
          legacyAgentModelOverrideKey(conversationId),
        )
      } catch {
        // localStorage may throw in private mode or with quota issues. The
        // dropdown still reflects in-memory state for the rest of the session.
      }
      setValue(next)
      setHasOverride(true)
    },
    [conversationId],
  )

  /** Merge a partial patch onto the current override and persist it. */
  const patch = useCallback(
    (delta: Partial<AgentOverride>) => {
      write({ ...value, ...delta })
    },
    [value, write],
  )

  /** Reset to the implicit project default (clears both storage keys). */
  const clear = useCallback(() => {
    if (!conversationId) return
    if (typeof window !== 'undefined') {
      try {
        window.localStorage.removeItem(agentOverrideKey(conversationId))
        window.localStorage.removeItem(
          legacyAgentModelOverrideKey(conversationId),
        )
      } catch {
        // ignore — fall back to in-memory reset below
      }
    }
    setValue(DEFAULT_AGENT_OVERRIDE)
    setHasOverride(false)
  }, [conversationId])

  return { value, hasOverride, patch, clear } as const
}

/**
 * Legacy reactive hook surface — returns the bare Cursor model id and a setter
 * matching the original {@code useAgentModelOverride} contract. Implemented on
 * top of {@link useAgentOverride} so both can coexist while call sites migrate.
 *
 * @deprecated Prefer {@link useAgentOverride}.
 */
export function useAgentModelOverride(conversationId: string | null) {
  const { value, patch, clear } = useAgentOverride(conversationId)
  const modelValue = value.backend === 'cursor' ? value.model : null
  const setOverride = useCallback(
    (next: string | null) => {
      if (next === null) {
        clear()
      } else {
        patch({ backend: 'cursor', model: next })
      }
    },
    [patch, clear],
  )
  return { value: modelValue, setOverride } as const
}

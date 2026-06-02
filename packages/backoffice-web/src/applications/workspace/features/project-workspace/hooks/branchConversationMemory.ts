const STORAGE_KEY = 'branchLastConversation.v1'

type BranchConversationMap = Record<string, string>

function loadMap(): BranchConversationMap {
  if (typeof window === 'undefined') return {}
  try {
    const raw = window.sessionStorage.getItem(STORAGE_KEY)
    if (!raw) return {}
    const parsed = JSON.parse(raw) as unknown
    if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed)) return {}
    const map: BranchConversationMap = {}
    for (const [branchId, conversationId] of Object.entries(parsed)) {
      if (
        typeof branchId === 'string' &&
        typeof conversationId === 'string' &&
        conversationId.length > 0
      ) {
        map[branchId] = conversationId
      }
    }
    return map
  } catch {
    return {}
  }
}

function persistMap(map: BranchConversationMap) {
  if (typeof window === 'undefined') return
  try {
    window.sessionStorage.setItem(STORAGE_KEY, JSON.stringify(map))
  } catch {
    // best-effort — storage quota or private mode
  }
}

export function getLastBranchConversationId(branchId: string): string | null {
  if (!branchId) return null
  return loadMap()[branchId] ?? null
}

export function setLastBranchConversationId(
  branchId: string,
  conversationId: string,
) {
  if (!branchId || !conversationId) return
  const map = loadMap()
  map[branchId] = conversationId
  persistMap(map)
}

export function clearLastBranchConversationId(branchId: string) {
  if (!branchId) return
  const map = loadMap()
  if (!(branchId in map)) return
  delete map[branchId]
  persistMap(map)
}

export function branchWorkspaceHref(
  slug: string,
  projectId: string,
  branchId: string,
  conversationId?: string | null,
): string {
  const c =
    conversationId !== undefined
      ? conversationId
      : getLastBranchConversationId(branchId)
  const base = `/w/${slug}/projects/${projectId}/branches/${branchId}`
  if (!c) return base
  return `${base}?${new URLSearchParams({ c }).toString()}`
}

import { afterEach, describe, expect, it } from 'vitest'
import {
  branchWorkspaceHref,
  clearLastBranchConversationId,
  getLastBranchConversationId,
  setLastBranchConversationId,
} from '../branchConversationMemory'

const STORAGE_KEY = 'branchLastConversation.v1'

afterEach(() => {
  window.sessionStorage.removeItem(STORAGE_KEY)
})

describe('branchConversationMemory', () => {
  it('stores and retrieves the last conversation for a branch', () => {
    setLastBranchConversationId('branch-a', 'conv-1')
    expect(getLastBranchConversationId('branch-a')).toBe('conv-1')
    expect(getLastBranchConversationId('branch-b')).toBeNull()
  })

  it('clears the stored conversation for a branch', () => {
    setLastBranchConversationId('branch-a', 'conv-1')
    clearLastBranchConversationId('branch-a')
    expect(getLastBranchConversationId('branch-a')).toBeNull()
  })

  it('builds branch href with remembered conversation', () => {
    setLastBranchConversationId('branch-a', 'conv-1')
    expect(branchWorkspaceHref('acme', 'proj-1', 'branch-a')).toBe(
      '/w/acme/projects/proj-1/branches/branch-a?c=conv-1',
    )
  })

  it('builds branch href without query when nothing is remembered', () => {
    expect(branchWorkspaceHref('acme', 'proj-1', 'branch-a')).toBe(
      '/w/acme/projects/proj-1/branches/branch-a',
    )
  })

  it('prefers an explicit conversation override in href builder', () => {
    setLastBranchConversationId('branch-a', 'conv-old')
    expect(branchWorkspaceHref('acme', 'proj-1', 'branch-a', 'conv-new')).toBe(
      '/w/acme/projects/proj-1/branches/branch-a?c=conv-new',
    )
  })
})

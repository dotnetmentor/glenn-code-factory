// Tests for McpRegistry — Spec 15 Card 5.

import { describe, expect, it } from 'vitest'

import { McpRegistry, type McpEntry } from './McpRegistry.js'

const A: McpEntry = {
  name: 'github',
  version: '0.1.0',
  baseUrl: 'http://localhost:5338/api/mcp/github',
}
const B: McpEntry = {
  name: 'kanban',
  version: '0.2.0',
  baseUrl: 'http://localhost:5338/api/mcp/kanban',
}
const C: McpEntry = {
  name: 'files',
  version: '0.3.0',
  baseUrl: 'http://localhost:5338/api/mcp/files',
}

describe('McpRegistry', () => {
  it('entries() is empty by default', () => {
    const reg = new McpRegistry()
    expect(reg.entries()).toEqual([])
  })

  it('loadInitial([a, b]) → entries() returns those two', () => {
    const reg = new McpRegistry()
    reg.loadInitial([A, B])
    expect(reg.entries()).toEqual([A, B])
  })

  it('replaceAll([c]) → entries() returns just c', () => {
    const reg = new McpRegistry()
    reg.loadInitial([A, B])
    reg.replaceAll([C])
    expect(reg.entries()).toEqual([C])
  })

  it('loadInitial defensively copies the input — mutating the caller array does not affect snapshot', () => {
    const reg = new McpRegistry()
    const input: McpEntry[] = [A, B]
    reg.loadInitial(input)
    // Caller mutates the array they passed in.
    input.push(C)
    // Registry snapshot is unchanged.
    expect(reg.entries()).toEqual([A, B])
  })

  it('replaceAll defensively copies the input', () => {
    const reg = new McpRegistry()
    const input: McpEntry[] = [A]
    reg.replaceAll(input)
    input.push(B)
    expect(reg.entries()).toEqual([A])
  })

  it('entries() returns a stable reference between calls when no mutation happens', () => {
    const reg = new McpRegistry()
    reg.loadInitial([A])
    const first = reg.entries()
    const second = reg.entries()
    expect(first).toBe(second)
  })
})

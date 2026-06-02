import { describe, expect, it } from 'vitest'

import { isDevPreviewService } from './devPreviewService.js'

describe('isDevPreviewService', () => {
  it('matches npm/vite dev-server commands', () => {
    expect(isDevPreviewService({ command: 'npm run dev -- --host 0.0.0.0' })).toBe(true)
    expect(isDevPreviewService({ command: 'npx vite --host 0.0.0.0' })).toBe(true)
    expect(isDevPreviewService({ command: 'dotnet run' })).toBe(false)
  })
})

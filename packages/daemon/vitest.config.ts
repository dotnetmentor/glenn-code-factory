import { readFileSync } from 'node:fs'
import { defineConfig } from 'vitest/config'

// Inline `.md` imports as raw string content, mirroring esbuild's
// `loader: { '.md': 'text' }` config (see esbuild.config.mjs). Without this,
// Vite's import analysis fails on `import x from './foo.md'`.
const markdownAsText = {
  name: 'markdown-as-text',
  enforce: 'pre' as const,
  transform(_code: string, id: string) {
    if (!id.endsWith('.md')) return null
    const content = readFileSync(id, 'utf8')
    return {
      code: `export default ${JSON.stringify(content)}`,
      map: null,
    }
  },
}

export default defineConfig({
  plugins: [markdownAsText],
  test: {
    environment: 'node',
    globals: true,
    coverage: {
      provider: 'v8',
      reporter: ['text', 'lcov'],
    },
    include: ['src/**/*.test.ts'],
  },
})

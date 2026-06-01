/// <reference types="vitest" />
import { defineConfig, type Plugin } from 'vitest/config'
import react from '@vitejs/plugin-react'
import path from 'path'

/**
 * Stub plugin that turns any side-effect `.css` import into an empty module.
 * Required for libraries that ship raw CSS imports (e.g. @mui/x-data-grid v8)
 * since Node ESM cannot import `.css` natively under Vitest.
 */
function stubCssImports(): Plugin {
  return {
    name: 'stub-css-imports',
    enforce: 'pre',
    resolveId(source) {
      if (source.endsWith('.css')) {
        return '\0virtual:empty-css'
      }
      return null
    },
    load(id) {
      if (id === '\0virtual:empty-css') {
        return 'export default {}'
      }
      return null
    },
  }
}

export default defineConfig({
  plugins: [stubCssImports(), react()],
  test: {
    environment: 'happy-dom',
    globals: true,
    setupFiles: ['./src/test-setup.ts'],
    css: false,
    server: {
      deps: {
        // Inline @mui/x-data-grid so its CSS side-effect imports run through
        // the stub plugin above (Vite only transforms inlined deps).
        inline: [/@mui\/x-data-grid/],
      },
    },
  },
  resolve: {
    alias: {
      '@': path.resolve(__dirname, './src'),
    },
  },
})

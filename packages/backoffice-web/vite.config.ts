import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'
import path from 'path'

export default defineConfig({
  plugins: [react()],
  resolve: {
    alias: {
      '@': path.resolve(__dirname, './src'),
    },
  },
  server: {
    host: '0.0.0.0',
    allowedHosts: ['.trycloudflare.com', '.playglenn.com', '.vibecodementor.net', '.glenncode.ai', '.glenncode.cc'],
    proxy: {
      '/api': {
        target: 'http://localhost:5338',
        changeOrigin: true,
        ws: true,
      },
      '/hubs': {
        target: 'http://localhost:5338',
        changeOrigin: true,
        ws: true,
      },
    },
  },
})

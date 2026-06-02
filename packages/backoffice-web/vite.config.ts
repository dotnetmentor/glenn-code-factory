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
    // `.trycloudflare.com` covers the quick tunnel started by `npm run dev`.
    // Add your own preview hostnames here if you serve the dev server behind them.
    allowedHosts: ['.trycloudflare.com'],
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

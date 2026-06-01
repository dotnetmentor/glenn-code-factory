import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { CssBaseline } from '@mui/material'
import './index.css'
import App from './App.tsx'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { AxiosError } from 'axios'
import { ThemeModeProvider } from './themes'
import { AuthProvider } from './auth/AuthProvider'
import { AuthGate } from './auth/AuthGate'
import { BrowserRouter } from 'react-router-dom'
import { ErrorBoundary } from './applications/shared/components/ErrorBoundary'
import { init as initPreviewBridge, notifyReady } from './lib/preview-bridge'

// Wire up the preview bridge (postMessage to studio + backend error reporting).
// Safe to call on any page — no-op if already initialized.
initPreviewBridge()

const queryClient = new QueryClient({
  defaultOptions: {
    queries: {
      gcTime: 90_000,
      retry: (failureCount, error) => {
        if (error instanceof AxiosError && error.response?.status === 404) {
          return false;
        }
        return failureCount < 3;
      },
    },
  },
})

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <ErrorBoundary>
      <BrowserRouter>
        <ThemeModeProvider>
          <CssBaseline />
          <QueryClientProvider client={queryClient}>
            <AuthProvider>
              <AuthGate>
                <App />
              </AuthGate>
            </AuthProvider>
          </QueryClientProvider>
        </ThemeModeProvider>
      </BrowserRouter>
    </ErrorBoundary>
  </StrictMode>,
)

notifyReady()

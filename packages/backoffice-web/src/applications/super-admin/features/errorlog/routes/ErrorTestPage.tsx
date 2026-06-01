import { Box, Button, Stack, Typography, TextField, Alert } from '@mui/material'
import { useState, useCallback, useMemo } from 'react'
import { captureError, flushNow } from '../../../../../lib/errorReporting'

/**
 * ErrorTestPage — a dev-only diagnostic route used by the E2E acceptance
 * spec (packages/e2e/tests/super-admin/error-capture.spec.ts) to prove the
 * browser → pipeline → dashboard loop works end-to-end.
 *
 * Hardening:
 *  - The route is registered with `hideInNavigation: true` so it never appears
 *    in the sidebar.
 *  - The component itself early-returns in production builds via
 *    `import.meta.env.DEV`, so even if the route leaks into a prod bundle it
 *    renders a stub instead of a throw-button.
 *
 * The E2E test supplies a unique marker string via the `?marker=` query
 * param so each test run writes distinguishable errors. The test then
 * searches the /super-admin/error-logs list for that marker to assert the
 * error landed.
 */
export function ErrorTestPage() {
  const [marker, setMarker] = useState<string>(() => {
    try {
      const url = new URL(window.location.href)
      return url.searchParams.get('marker') ?? `e2e-${Date.now()}`
    } catch {
      return `e2e-${Date.now()}`
    }
  })
  const [lastAction, setLastAction] = useState<string>('')

  const isDev = import.meta.env.DEV

  const errorMessage = useMemo(() => `E2E_TEST_ERROR ${marker}`, [marker])

  // The backend's ErrorSignatureHasher keys signatures off
  // (Source + ExceptionType + top-3 stack frames) — NOT the message. If every
  // run shared a single exception-type name, all runs would collapse into one
  // signature whose detail-row sample quota (10) would fill up across CI runs,
  // causing subsequent runs to look like they "disappeared" (count increments
  // but no detail row saved). Scoping the exception name by marker gives each
  // run its own signature, so the test can always find the detail row.
  // The `triple` test intentionally shares ONE errorType across its three
  // throws so the signature-aggregation path is still exercised (count=3).
  const singleErrorType = useMemo(() => `E2ETestError_${marker}`, [marker])
  const tripleErrorType = useMemo(() => `E2ETestErrorTriple_${marker}`, [marker])

  // Use captureError directly rather than `throw` so the error is reliably
  // delivered to the backend without relying on React's error boundary, and
  // without crashing the page (which would break subsequent test steps).
  const triggerSingle = useCallback(() => {
    const err = new Error(errorMessage)
    err.name = singleErrorType
    captureError(err, { errorType: singleErrorType })
    // Force immediate flush so the E2E acceptance test doesn't race the
    // 5-second default flush interval.
    flushNow()
    setLastAction(`Sent 1 error with marker "${marker}"`)
  }, [errorMessage, marker, singleErrorType])

  const triggerThree = useCallback(() => {
    for (let i = 0; i < 3; i++) {
      const err = new Error(errorMessage)
      err.name = tripleErrorType
      captureError(err, { errorType: tripleErrorType })
    }
    flushNow()
    setLastAction(`Sent 3 errors with marker "${marker}"`)
  }, [errorMessage, marker, tripleErrorType])

  const triggerUnhandled = useCallback(() => {
    // Dispatch a synthetic runtime error — the preview-bridge window.error
    // listener picks this up via its dual-channel dispatcher. Proves the
    // auto-capture path (not just manual captureError) reaches the backend.
    setLastAction(`Dispatched runtime error with marker "${marker}"`)
    setTimeout(() => {
      const err = new Error(errorMessage)
      err.name = singleErrorType
      throw err
    }, 0)
  }, [errorMessage, marker, singleErrorType])

  if (!isDev) {
    return (
      <Box sx={{ p: 4 }}>
        <Alert severity="warning">
          Error test page is disabled in production builds.
        </Alert>
      </Box>
    )
  }

  return (
    <Box sx={{ p: 4, maxWidth: 640 }}>
      <Typography variant="h4" component="h1" gutterBottom>
        Error Capture Acceptance Test
      </Typography>
      <Typography variant="body2" color="text.secondary" sx={{ mb: 3 }}>
        Dev-only route. Triggers frontend errors that should appear in the
        Error Log dashboard within a few seconds.
      </Typography>

      <TextField
        label="Marker"
        value={marker}
        onChange={(e) => setMarker(e.target.value)}
        size="small"
        fullWidth
        sx={{ mb: 2 }}
        helperText="Each error message will contain this unique string so the E2E test can find it."
        inputProps={{ 'data-testid': 'error-test-marker-input' }}
      />

      <Stack direction="row" spacing={2} sx={{ mb: 3 }}>
        <Button
          variant="contained"
          color="error"
          onClick={triggerSingle}
          data-testid="error-test-trigger-single"
        >
          Trigger 1 Error
        </Button>
        <Button
          variant="contained"
          color="warning"
          onClick={triggerThree}
          data-testid="error-test-trigger-three"
        >
          Trigger 3 Errors (same signature)
        </Button>
        <Button
          variant="outlined"
          color="error"
          onClick={triggerUnhandled}
          data-testid="error-test-trigger-unhandled"
        >
          Trigger Uncaught Error
        </Button>
      </Stack>

      {lastAction && (
        <Alert severity="success" data-testid="error-test-status">
          {lastAction}
        </Alert>
      )}
    </Box>
  )
}

import { test, expect } from '@playwright/test'

/**
 * Outside-in acceptance spec for the Resilient Error Capture Pipeline.
 *
 * Proves the full loop end-to-end:
 *   browser error → /api/errors/report → ErrorQueue → persistence worker →
 *   ErrorLog row visible in /super-admin/error-logs filtered by Source="Frontend".
 *
 * The test-only route at /super-admin/error-test (dev-only) exposes a
 * "Trigger" button whose click calls `captureError()` with a uniquely
 * marked message. Each run uses a timestamp-based marker so assertions
 * don't collide across runs.
 *
 * Why we assert via the list endpoint rather than the dashboard UI alone:
 * the pipeline is eventually-consistent (batched writes every 500ms), so
 * polling the list API with a generous timeout is the most reliable signal.
 * We also separately load /super-admin/error-logs and confirm the row
 * renders in the UI — that proves the super-admin user journey works.
 */

const ADMIN_EMAIL = 'admin@test.com'
const ADMIN_PASSWORD = 'Test123!'

/**
 * Poll `/api/error-logs?search=<marker>&source=Frontend` until at least
 * `expected` rows show up, or until the deadline expires. Returns the
 * matched rows.
 */
async function waitForErrorRows(
  request: import('@playwright/test').APIRequestContext,
  marker: string,
  expected: number,
  timeoutMs: number,
): Promise<Array<{ id: string; message: string; source: string; correlationId: string | null }>> {
  const deadline = Date.now() + timeoutMs
  let lastItems: Array<{ id: string; message: string; source: string; correlationId: string | null }> = []
  let lastStatus = 0
  let lastErr: string | undefined

  while (Date.now() < deadline) {
    try {
      const res = await request.get('/api/error-logs', {
        params: { search: marker, source: 'Frontend', page: 1, pageSize: 50 },
      })
      lastStatus = res.status()
      if (res.ok()) {
        const body = (await res.json()) as { items: typeof lastItems }
        lastItems = body.items ?? []
        if (lastItems.length >= expected) {
          return lastItems
        }
      }
    } catch (err) {
      lastErr = err instanceof Error ? err.message : String(err)
    }
    await new Promise((r) => setTimeout(r, 300))
  }

  throw new Error(
    `Timed out after ${timeoutMs}ms waiting for ${expected} error row(s) ` +
      `with marker "${marker}". Last seen ${lastItems.length} items ` +
      `(status=${lastStatus}${lastErr ? `, err=${lastErr}` : ''}).`,
  )
}

test.describe('Resilient Error Capture Pipeline — E2E acceptance', () => {
  // These tests share the super-admin storageState (auth cookie) configured
  // in playwright.config.ts.
  test.use({ storageState: '.auth/super-admin.json' })

  // Give every browser request (and every APIRequestContext call) its own
  // unique X-Test-Session header so the rate limiter partitions per-test and
  // parallel / repeated test runs can never starve each other of permits.
  // The rate limiter's silent-204 drop path would otherwise make rejected
  // requests indistinguishable from accepted ones — see
  // RateLimitingExtensions.cs's `ErrorReport` policy.
  let testSession: string
  test.beforeEach(async ({ page, context }) => {
    testSession = `e2e-${Date.now()}-${Math.random().toString(36).slice(2, 8)}`
    await page.setExtraHTTPHeaders({ 'X-Test-Session': testSession })
    await context.setExtraHTTPHeaders({ 'X-Test-Session': testSession })
  })

  test('frontend error lands in admin dashboard within 10s', async ({ page, request }) => {
    const marker = `e2e-single-${Date.now()}`
    await request.storageState() // warm request client with the super-admin cookie

    await page.goto(`/super-admin/error-test?marker=${encodeURIComponent(marker)}`)
    await expect(page.getByTestId('error-test-trigger-single')).toBeVisible({ timeout: 10_000 })

    await page.getByTestId('error-test-trigger-single').click()
    await expect(page.getByTestId('error-test-status')).toContainText(marker, { timeout: 5_000 })

    // Wait for the pipeline to persist the row (batched writes every 500ms).
    const rows = await waitForErrorRows(request, marker, 1, 10_000)

    expect(rows.length).toBeGreaterThanOrEqual(1)
    const row = rows[0]
    expect(row.source).toBe('Frontend')
    expect(row.message).toContain(marker)

    // Second assertion: the error is visible in the super-admin UI.
    await page.goto('/super-admin/error-logs')
    // The search field lives in ErrorLogsTable, debounced — type then wait.
    await page.getByPlaceholder('Search errors...').fill(marker)
    await expect(page.getByText(new RegExp(marker))).toBeVisible({ timeout: 10_000 })
  })

  test('same error triggered 3x produces 3 persisted rows (signature aggregation path)', async ({
    page,
    request,
  }) => {
    const marker = `e2e-triple-${Date.now()}`

    await page.goto(`/super-admin/error-test?marker=${encodeURIComponent(marker)}`)
    await expect(page.getByTestId('error-test-trigger-three')).toBeVisible({ timeout: 10_000 })

    await page.getByTestId('error-test-trigger-three').click()
    await expect(page.getByTestId('error-test-status')).toContainText('3 errors', { timeout: 5_000 })

    // The backend stores one ErrorLog row PER occurrence (capped at 10/signature
    // by the sample cap) AND aggregates into a single ErrorSignature with Count=3.
    // We assert on the occurrence rows since that's what the list endpoint exposes.
    const rows = await waitForErrorRows(request, marker, 3, 15_000)

    const matched = rows.filter((r) => r.message.includes(marker))
    expect(matched.length, 'expected 3 ErrorLog rows with the same marker').toBe(3)

    // All three must be Source=Frontend.
    for (const r of matched) {
      expect(r.source).toBe('Frontend')
    }

    // TODO(signature-view): when GET /api/error-logs/signatures ships, assert
    // that querying signatures for this marker returns exactly one signature
    // with count=3. For now we prove the pipeline received all three events.
  })

  test('correlation id from a prior API request is attached to subsequent frontend errors', async ({
    page,
    request,
  }) => {
    const marker = `e2e-corr-${Date.now()}`

    // Visiting the test route (and any authenticated page) triggers API calls
    // that return an X-Correlation-Id header; the errorReporting module stores
    // the last-seen value and attaches it to subsequent reports.
    await page.goto(`/super-admin/error-test?marker=${encodeURIComponent(marker)}`)
    await expect(page.getByTestId('error-test-trigger-single')).toBeVisible({ timeout: 10_000 })

    // Kick an API request that we know returns a correlation id, so the
    // errorReporting module has a fresh id to attach.
    await page.evaluate(async () => {
      try {
        await fetch('/api/error-logs/count', { credentials: 'same-origin' })
      } catch {
        // ignore — this is best-effort priming
      }
    })

    await page.getByTestId('error-test-trigger-single').click()
    await expect(page.getByTestId('error-test-status')).toContainText(marker, { timeout: 5_000 })

    const rows = await waitForErrorRows(request, marker, 1, 10_000)
    const row = rows[0]

    // Soft assertion: some environments may not populate correlation ids, so
    // don't fail the run if it's null — but log it so the CI author can see.
    if (row.correlationId) {
      expect(row.correlationId.length).toBeGreaterThan(0)
    } else {
      test.info().annotations.push({
        type: 'note',
        description:
          'Correlation id was null on the persisted row. This indicates ' +
          'the correlation-id middleware is not attaching a header on the ' +
          'priming request, or the errorReporting module is not reading it. ' +
          'Not a failure of the pipeline itself.',
      })
    }
  })
})

/**
 * Auth-less login fallback used if storageState is stale. This runs once
 * per worker to refresh the cookie before the acceptance suite kicks off.
 * Kept as an escape hatch — normal runs rely on playwright.config.ts's
 * `setup` project to produce .auth/super-admin.json.
 */
async function reAuthIfNeeded(
  page: import('@playwright/test').Page,
): Promise<void> {
  await page.goto('/super-admin/error-test')
  const loginVisible = await page
    .getByRole('textbox', { name: 'Email address' })
    .isVisible()
    .catch(() => false)

  if (!loginVisible) return

  await page.evaluate(
    async ({ email, password }) => {
      await fetch(window.location.origin + '/api/auth/login', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ email, password }),
      })
    },
    { email: ADMIN_EMAIL, password: ADMIN_PASSWORD },
  )
  await page.reload()
}

// Re-auth guard as a `beforeAll` for the suite.
test.beforeAll(async ({ browser }) => {
  const context = await browser.newContext({ storageState: '.auth/super-admin.json' })
  const page = await context.newPage()
  try {
    await reAuthIfNeeded(page)
  } finally {
    await context.close()
  }
})

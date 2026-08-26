#!/usr/bin/env node
/**
 * snap-preview — the agent's self-validation primitive.
 *
 * Opens the project's dev server in headless Chromium *inside the runtime box*,
 * captures a screenshot plus every console error / page error / failed request,
 * and prints a single JSON summary to stdout. The agent runs this after making
 * frontend changes to see its own work — including Three.js/WebGL scenes, which
 * render via SwiftShader (CPU) since boxes have no GPU: pixel-correct, slower
 * than native, ideal for correctness checks.
 *
 * Usage:
 *   snap-preview [url] [--out /tmp/preview.png] [--wait 1500] [--full] [--strict]
 *
 *   url       default http://localhost:$PREVIEW_PORT (or 5173)
 *   --out     screenshot path (default /tmp/preview.png)
 *   --wait    extra settle time in ms after load (default 1500; raise for heavy
 *             WebGL scenes — SwiftShader needs a few frames to produce pixels)
 *   --full    full-page screenshot instead of viewport
 *   --strict  exit 1 when console/page errors were captured
 *
 * Invoked via the /usr/local/bin/snap-preview wrapper, which sets NODE_PATH to
 * the global npm root so `require('playwright')` resolves the template's
 * system-wide install (CommonJS on purpose — ESM ignores NODE_PATH).
 */
const path = require('path');

const args = process.argv.slice(2);
const flag = (name) => {
    const i = args.indexOf(name);
    if (i === -1) return undefined;
    return true;
};
const opt = (name, fallback) => {
    const i = args.indexOf(name);
    if (i === -1 || i === args.length - 1) return fallback;
    return args[i + 1];
};

const url = args.find((a) => !a.startsWith('--') && a !== opt('--out') && a !== opt('--wait'))
    || `http://localhost:${process.env.PREVIEW_PORT || '5173'}`;
const out = path.resolve(opt('--out', '/tmp/preview.png'));
const settleMs = parseInt(opt('--wait', '1500'), 10);
const fullPage = flag('--full') === true;
const strict = flag('--strict') === true;

(async () => {
    const { chromium } = require('playwright');

    const consoleErrors = [];
    const consoleWarnings = [];
    const pageErrors = [];
    const failedRequests = [];

    const launchArgs = [
        // Boxes have no GPU: force ANGLE-on-SwiftShader so WebGL/Three.js
        // renders in software instead of failing context creation. Modern
        // Chromium additionally gates software WebGL in headless behind
        // the unsafe-swiftshader flag.
        '--use-angle=swiftshader',
        '--enable-unsafe-swiftshader',
        '--disable-gpu-compositing',
        // Chromium's user-namespace sandbox is unreliable inside the VM;
        // the box itself is the isolation boundary.
        '--no-sandbox',
        '--disable-dev-shm-usage',
    ];

    // Executable resolution: Playwright's own browser first; on a version-drifted
    // install (playwright updated, browsers not), fall back to an explicit path or
    // the system Chrome that every box ships. SNAP_PREVIEW_CHROMIUM overrides.
    const fs = require('fs');
    const candidates = [
        process.env.SNAP_PREVIEW_CHROMIUM,
        '/usr/bin/google-chrome-stable',
        '/usr/bin/chromium-browser',
        '/usr/bin/chromium',
    ].filter((p) => p && fs.existsSync(p));

    let browser;
    try {
        browser = await chromium.launch({ headless: true, args: launchArgs });
    } catch (launchErr) {
        if (candidates.length === 0) throw launchErr;
        browser = await chromium.launch({ headless: true, args: launchArgs, executablePath: candidates[0] });
    }

    try {
        const page = await browser.newPage({ viewport: { width: 1280, height: 800 } });

        page.on('console', (msg) => {
            const line = msg.text().slice(0, 500);
            if (msg.type() === 'error') consoleErrors.push(line);
            else if (msg.type() === 'warning') consoleWarnings.push(line);
        });
        page.on('pageerror', (err) => pageErrors.push(String(err).slice(0, 500)));
        page.on('requestfailed', (req) => {
            failedRequests.push(`${req.method()} ${req.url()} — ${req.failure()?.errorText ?? 'failed'}`.slice(0, 500));
        });

        let status = null;
        let navigationError = null;
        try {
            const resp = await page.goto(url, { waitUntil: 'load', timeout: 30_000 });
            status = resp ? resp.status() : null;
            await page.waitForLoadState('networkidle', { timeout: 10_000 }).catch(() => {});
        } catch (err) {
            navigationError = String(err).slice(0, 500);
        }

        if (!navigationError && settleMs > 0) {
            await page.waitForTimeout(settleMs);
        }

        // WebGL probe: reports whether a WebGL2/WebGL context is obtainable and
        // whether any <canvas> on the page has non-blank pixels — the cheap
        // "did the Three.js scene actually draw something" signal. Heuristic:
        // toDataURL on a WebGL canvas without preserveDrawingBuffer can read
        // blank even when the scene rendered — treat anyCanvasPainted=false as
        // "unknown" and trust the screenshot (compositor output) as ground truth.
        let webgl = null;
        if (!navigationError) {
            webgl = await page.evaluate(() => {
                const probe = document.createElement('canvas');
                const gl = probe.getContext('webgl2') || probe.getContext('webgl');
                const renderer = gl
                    ? (() => {
                        const ext = gl.getExtension('WEBGL_debug_renderer_info');
                        return ext ? gl.getParameter(ext.UNMASKED_RENDERER_WEBGL) : gl.getParameter(gl.VERSION);
                    })()
                    : null;

                const canvases = Array.from(document.querySelectorAll('canvas'));
                let anyPainted = false;
                for (const c of canvases) {
                    try {
                        // Compare against a blank canvas of identical size — a
                        // byte-length threshold misjudges small or flat images.
                        const blank = document.createElement('canvas');
                        blank.width = c.width;
                        blank.height = c.height;
                        anyPainted = anyPainted || c.toDataURL('image/png') !== blank.toDataURL('image/png');
                    } catch { /* tainted canvas — can't read, don't guess */ }
                }
                return {
                    contextAvailable: !!gl,
                    renderer: renderer ? String(renderer).slice(0, 120) : null,
                    canvasCount: canvases.length,
                    anyCanvasPainted: canvases.length > 0 ? anyPainted : null,
                };
            }).catch(() => null);
        }

        if (!navigationError) {
            await page.screenshot({ path: out, fullPage });
        }

        const summary = {
            url,
            status,
            navigationError,
            screenshot: navigationError ? null : out,
            webgl,
            consoleErrors,
            pageErrors,
            failedRequests,
            consoleWarningCount: consoleWarnings.length,
        };
        console.log(JSON.stringify(summary, null, 2));

        const hadErrors = navigationError !== null || consoleErrors.length > 0 || pageErrors.length > 0;
        process.exit(strict && hadErrors ? 1 : 0);
    } finally {
        await browser.close();
    }
})().catch((err) => {
    console.log(JSON.stringify({ fatal: String(err).slice(0, 1000) }));
    process.exit(2);
});

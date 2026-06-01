import * as esbuild from 'esbuild'
import { readFileSync } from 'node:fs'
import { fileURLToPath } from 'node:url'
import { dirname, resolve } from 'node:path'

const __dirname = dirname(fileURLToPath(import.meta.url))
const pkg = JSON.parse(readFileSync(resolve(__dirname, 'package.json'), 'utf8'))
const watch = process.argv.includes('--watch')

const options = {
  entryPoints: [resolve(__dirname, 'src/main.ts')],
  bundle: true,
  outfile: resolve(__dirname, 'dist/main.js'),
  platform: 'node',
  target: 'node20',
  format: 'esm',
  sourcemap: true,
  minify: process.env.NODE_ENV === 'production',
  define: {
    __VERSION__: JSON.stringify(pkg.version),
  },
  banner: {
    js: "import { createRequire } from 'node:module'; const require = createRequire(import.meta.url);",
  },
  external: ['@cursor/sdk'],
  loader: {
    '.md': 'text',
  },
  logLevel: 'info',
}

if (watch) {
  const ctx = await esbuild.context(options)
  await ctx.watch()
  console.log('esbuild: watching for changes…')
} else {
  await esbuild.build(options)
}

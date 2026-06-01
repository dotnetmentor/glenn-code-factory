// Ambient module declaration so TypeScript accepts `import x from './foo.md'`.
// esbuild's `.md` text loader (configured in `esbuild.config.mjs`) bundles
// markdown files as their raw string contents.

declare module '*.md' {
  const content: string
  export default content
}

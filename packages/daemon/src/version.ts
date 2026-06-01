// __VERSION__ is replaced at build time by esbuild's `define`.
declare const __VERSION__: string
export const DAEMON_VERSION: string = typeof __VERSION__ === 'string' ? __VERSION__ : '0.0.0-dev'

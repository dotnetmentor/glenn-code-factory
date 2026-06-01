// Platform harness — attached to every Cursor turn so the model has a stable
// picture of the runtime it's in.
//
// esbuild bundles `harness.md` inline as a string via the `.md` -> `text` loader
// (see `esbuild.config.mjs`); at runtime there is no filesystem read.

import harnessMarkdown from './harness.md'

/**
 * Return the platform harness markdown for injection into the first turn prompt.
 */
export function getHarness(): string {
  return harnessMarkdown
}

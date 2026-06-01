/**
 * The four scopes the Changes tab can compare against.
 *
 * <p>A discriminated union so call sites can pattern-match on
 * {@code kind} without optional-chaining through differently-shaped
 * fields. Phase 1 only ever constructs {@code workingTree} — the other
 * three variants are typed up-front so the picker (Phase 3) can land
 * without retyping the surface.</p>
 *
 * <ul>
 *   <li>{@code workingTree} — uncommitted edits in the runtime's working
 *       tree (the agent's WIP).</li>
 *   <li>{@code branch} — everything this branch has done relative to
 *       {@code base} (typically {@code main}).</li>
 *   <li>{@code commit} — a single commit, equivalent to
 *       {@code sha^..sha}.</li>
 *   <li>{@code range} — an arbitrary {@code base..head} range a
 *       power-user dialled in.</li>
 * </ul>
 */
export type CompareScope =
  | { kind: 'workingTree' }
  | { kind: 'branch'; base: string }
  | { kind: 'commit'; sha: string }
  | { kind: 'range'; base: string; head: string }

/**
 * Wire string the backend's {@code GetApiRuntimesRuntimeIdDiffChangedFiles}
 * endpoint expects on the {@code scope} query param. Centralised so the
 * mapping lives next to the type — and the matching helper for the
 * {@code base} / {@code head} params lives next to it.
 */
export function scopeToWireParams(scope: CompareScope): {
  scope: string
  base?: string
  head?: string
} {
  switch (scope.kind) {
    case 'workingTree':
      return { scope: 'workingTree' }
    case 'branch':
      return { scope: 'branch', base: scope.base, head: 'HEAD' }
    case 'commit':
      // Commit-picker mode is really "branch-compare with a SHA as base":
      // diff from the picked commit's SHA forward to HEAD.
      return { scope: 'branch', base: scope.sha, head: 'HEAD' }
    case 'range':
      return { scope: 'range', base: scope.base, head: scope.head }
  }
}

/**
 * Default scope when the Changes tab first mounts.
 *
 * <p>Auto-commit-every-turn makes the working tree almost always empty,
 * so the calm default is "compare this branch's HEAD against {@code main}".
 * The picker promotes other scopes from here.</p>
 */
export const DEFAULT_COMPARE_SCOPE: CompareScope = {
  kind: 'branch',
  base: 'main',
}

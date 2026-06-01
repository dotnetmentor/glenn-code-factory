/**
 * Shape of the parsed proposal payload rendered by the proposal card.
 *
 * <p>This is a UI-side projection — three flat string buckets that the
 * {@code SpecChips} component can render as pill rows. The backend stores
 * the proposal as a JSON blob; this module converts whatever shape it
 * finds into the bucket form.</p>
 *
 * <p>Two backend formats are recognised:</p>
 * <ul>
 *   <li><b>Legacy V1 (StackPickerValue)</b> — {@code {languages, services,
 *       extras}} where each is a flat {@code string[]}. Authored by the
 *       Card-7 onboarding form and the original {@code propose_runtime_spec}
 *       payloads. Pass-through.</li>
 *   <li><b>V3 RuntimeSpecV3 (preset-based)</b> —
 *       {@code {version: 3, services: [{kind, name, values}]}} where each
 *       service references a {@code ServicePreset.slug}. Projected into
 *       buckets by extracting service names into {@code services} and
 *       deriving language hints from the preset kind family
 *       ({@code dotnet-mise → dotnet}, {@code node-vite → node}, etc.).
 *       {@code extras} is left empty — V3 dropped the addon concept.</li>
 * </ul>
 *
 * <p>Anything else (malformed, unknown version) collapses to all-empty —
 * never throws. The card will show "no additions" instead of crashing.</p>
 */
export interface ProposedSpec {
  languages: string[]
  services: string[]
  extras: string[]
}

const EMPTY: ProposedSpec = { languages: [], services: [], extras: [] }

/**
 * Best-effort mapping from a V3 preset {@code kind} to a UI-friendly
 * language/runtime tag. New presets can be added here as they ship — the
 * mapping is opt-in (unknown kinds simply don't contribute a language
 * chip; the service name still shows up in the services bucket).
 *
 * <p>The mapping is intentionally coarse — we want one chip per runtime
 * family, not one per preset variant. {@code dotnet-mise} and
 * {@code dotnet-mise-copy} both collapse to {@code dotnet}.</p>
 */
function languageFromKind(kind: string): string | null {
  if (kind.startsWith('dotnet')) return 'dotnet'
  if (kind.startsWith('node')) return 'node'
  if (kind.startsWith('postgres')) return 'postgres'
  if (kind.startsWith('redis')) return 'redis'
  if (kind.startsWith('python')) return 'python'
  return null
}

function asStringArray(value: unknown): string[] {
  if (!Array.isArray(value)) return []
  return value.filter((v): v is string => typeof v === 'string')
}

/**
 * Detect-and-project a V3 RuntimeSpecV3 payload. Returns {@code null} if
 * the input doesn't look like V3 — callers fall through to the legacy V1
 * parser.
 */
function parseV3(obj: Record<string, unknown>): ProposedSpec | null {
  if (obj.version !== 3) return null
  const rawServices = Array.isArray(obj.services) ? obj.services : []
  const serviceNames: string[] = []
  const languages = new Set<string>()
  for (const svc of rawServices) {
    if (!svc || typeof svc !== 'object') continue
    const sObj = svc as Record<string, unknown>
    const name = typeof sObj.name === 'string' ? sObj.name : null
    if (name) serviceNames.push(name)
    const kind = typeof sObj.kind === 'string' ? sObj.kind : null
    if (kind) {
      const lang = languageFromKind(kind)
      if (lang) languages.add(lang)
    }
  }
  return {
    languages: Array.from(languages).sort(),
    services: serviceNames,
    extras: [],
  }
}

/**
 * Tolerantly parses a proposal JSON blob into the {@link ProposedSpec}
 * UI projection. Recognises both legacy V1 ({@code StackPickerValue}) and
 * V3 ({@code RuntimeSpecV3}) shapes — see file-level docs for the
 * mapping.
 *
 * <p>Bad input (missing keys, non-array values, non-JSON, unknown
 * version) collapses to empty arrays — never throws — so a malformed or
 * future-version historical row doesn't blow up the proposal card.</p>
 */
export function parseProposedSpec(raw: string | null | undefined): ProposedSpec {
  if (!raw) return EMPTY
  try {
    const parsed = JSON.parse(raw) as unknown
    if (!parsed || typeof parsed !== 'object') return EMPTY
    const obj = parsed as Record<string, unknown>

    // V3 first — version-tagged shape, unambiguous.
    const v3 = parseV3(obj)
    if (v3) return v3

    // Fall back to V1 StackPickerValue. Each field independent — missing
    // keys collapse to empty arrays via asStringArray.
    return {
      languages: asStringArray(obj.languages),
      services: asStringArray(obj.services),
      extras: asStringArray(obj.extras),
    }
  } catch {
    return EMPTY
  }
}

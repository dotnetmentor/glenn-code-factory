/**
 * Shared types for the tool formatter registry.
 *
 * <p>Card 5 / cursor-native-chat-ux §4: a single typed module turns each
 * {@link ToolUseEventDto} into three render-ready strings (active label while
 * running, summary when completed, error variant on failure) plus an optional
 * MUI icon name. The pill UI (card 7) consumes these as the single source of
 * truth for what a tool is doing — no raw JSON in the default view.</p>
 *
 * <p>Formatters MUST be defensive: missing {@code args} / {@code result},
 * malformed JSON, missing optional fields all degrade gracefully through the
 * fallback formatter or sensible defaults. They never throw — the chat
 * surface stays alive even when the wire payload is wrong.</p>
 */
import type { ToolUseEventDto } from '../../../../../../api/queries-commands'

/**
 * The three labels produced for one tool event, plus an optional MUI icon
 * name. The icon name maps 1:1 to {@code @mui/icons-material} exports — the
 * pill UI looks them up lazily so unknown names degrade to no icon.
 */
export interface FormatterOutput {
  /** Shown in the pill while {@code status === 'Running'}. */
  activeLabel: string
  /** Shown in the pill + trace once {@code status === 'Completed'}. */
  summary: string
  /** Shown when {@code status === 'Error'}. */
  errorVariant: string
  /** Optional MUI icon name (e.g. {@code 'Terminal'}, {@code 'Description'}). */
  glyph?: string
}

/**
 * Signature every formatter implements. The event is the raw wire DTO so each
 * formatter owns its own argument parsing — the registry doesn't try to
 * pre-parse {@code args}/{@code result} since the shape is tool-specific.
 */
export type ToolFormatter = (event: ToolUseEventDto) => FormatterOutput

/**
 * Re-export so consumers (registry, tests) can import a single barrel.
 */
export type { ToolUseEventDto }

/**
 * Re-export of the cursor-native `MappedCursorEvent` shape. The daemon's
 * `AgentFactory` yields these directly post card-3.5 rewrite — no more
 * intermediate Claude-flavored translation.
 *
 * Kept under the `TurnEvent` name because dozens of imports reference it;
 * the shape is exactly what `CursorEventMapper` emits.
 */
export type { MappedCursorEvent as TurnEvent } from './CursorEventMapper.js'

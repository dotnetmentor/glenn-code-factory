import type { TurnEvent } from './TurnEvent.js'
import type { TurnOptions } from './TurnOptions.js'

/**
 * The thin seam the Cursor factory implements: turn options → event stream.
 */
export type AgentFactory = (opts: TurnOptions) => AsyncIterable<TurnEvent>

export type { TurnEvent, TurnOptions }

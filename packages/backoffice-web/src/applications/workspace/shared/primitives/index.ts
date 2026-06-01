/**
 * Workspace primitives — the small, reusable building blocks the rest of the
 * polish depends on. Built in isolation in Phase 1; visible all-states on the
 * {@code /w/:slug/playground} route.
 *
 * <p>Consumers should import from this barrel so the surface stays stable as
 * primitives are added.
 */

export { StatusDot, type StatusDotProps } from './StatusDot'
export { RuntimePill, type RuntimePillProps, type RuntimePillState } from './RuntimePill'
export {
  SegmentedTabs,
  type SegmentedTabsProps,
  type SegmentedTabItem,
} from './SegmentedTabs'
export { FlatSwitch, type FlatSwitchProps } from './FlatSwitch'
export { KbdChip, type KbdChipProps } from './KbdChip'
export { InlineCode, type InlineCodeProps } from './InlineCode'
export { IdChip, type IdChipProps } from './IdChip'
export { IconTile, type IconTileProps, type IconTileTone } from './IconTile'
export {
  UnderlineTabs,
  type UnderlineTabsProps,
  type UnderlineTabItem,
} from './UnderlineTabs'

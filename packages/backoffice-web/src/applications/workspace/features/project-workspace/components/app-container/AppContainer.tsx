import { useState, type ComponentType, type SVGProps } from 'react'
import { Box } from '@mui/material'
import PublicOutlinedIcon from '@mui/icons-material/PublicOutlined'
import DifferenceOutlinedIcon from '@mui/icons-material/DifferenceOutlined'
import ChecklistOutlinedIcon from '@mui/icons-material/ChecklistOutlined'
import ViewKanbanOutlinedIcon from '@mui/icons-material/ViewKanbanOutlined'
import type { SvgIconProps } from '@mui/material/SvgIcon'
import { RuntimeState } from '../../../../../../api/queries-commands'
import type { AgentHubConnection } from '../../../../../../lib/signalr'
import { appContainerTokens } from './tokens'
import { PreviewTab } from './PreviewTab'
import { ChangesTab } from './changes/ChangesTab'
import { SpecsTab } from './SpecsTab'
import { KanbanTab } from './KanbanTab'

interface AppContainerProps {
  projectId: string
  branchId: string
  /**
   * Active runtime id for this branch. Used by the Changes tab to scope
   * its diff queries (the diff REST endpoints are runtime-scoped). May
   * be empty during initial loads — the tab renders its offline state
   * until a real id arrives.
   */
  runtimeId: string
  /** Branch's preview subdomain (hostname only — no scheme). Null when absent. */
  previewHostname: string | null
  /** Current runtime state — drives the preview empty state. */
  runtimeState: RuntimeState | string | undefined
  /**
   * Shared AgentHub connection lifted from the route. When present, the
   * Preview tab subscribes to {@code previewPortChanged} pushes and
   * key-bumps the iframe so the live preview refreshes against the new
   * Cloudflare ingress automatically. {@code null} for callers that haven't
   * established the hub yet — the tab simply skips the subscription.
   */
  connection: AgentHubConnection | null
  /**
   * Display name of the active branch. Threaded into the Changes tab's
   * CompareAgainstPicker so it can detect the "you're on the base"
   * edge case (e.g. user is on {@code main} and tries to compare to
   * {@code main}).
   */
  currentBranch?: string
  /**
   * Optionally make the active tab a controlled value. When provided, the
   * AppContainer renders the supplied tab and never mutates its own
   * internal state — used by the mobile composition that drives Preview /
   * Changes selection from an outer 3-tab bar. Leave undefined to keep
   * legacy uncontrolled behavior.
   */
  activeTab?: AppTabId
  /**
   * Called whenever a tab is selected from the bottom strip. Wired only
   * when the caller wants to sync controlled state back — uncontrolled
   * callers can ignore it.
   */
  onActiveTabChange?: (id: AppTabId) => void
  /**
   * When {@code true}, the bottom tab strip is suppressed entirely. Used
   * by the mobile composition where an outer Chat / Preview / Changes bar
   * supersedes the internal Preview / Changes strip.
   */
  hideTabStrip?: boolean
  /**
   * Optional count badge per tab. Threaded from the parent route once
   * counts are available (e.g. specs count, kanban card count, diff
   * file count). Omit a key to suppress the badge for that tab.
   */
  tabCounts?: Partial<Record<AppTabId, number>>
}

/**
 * Glenn-style AppContainer — the right-hand panel of the workspace.
 *
 * <p>Holds four tabs that stay always-mounted (visibility toggle via
 * {@code display: none}) so switching never blows the user's in-page
 * state inside the running app's iframe.</p>
 *
 * <p>The bottom strip is a {@link SegmentedTabs} pill — a single
 * chip-coloured container with rounded child buttons that flip to a
 * raised "panel" look when active. Telemetry text on the right gives the
 * user a glance-able status string per tab (active port, file count,
 * etc.) — kept in mono with the {@code textGhost} colour so it reads as
 * info, not a button.</p>
 */
export function AppContainer({
  projectId,
  branchId,
  runtimeId,
  previewHostname,
  runtimeState,
  connection,
  currentBranch,
  activeTab: controlledActiveTab,
  onActiveTabChange,
  hideTabStrip = false,
  tabCounts,
}: AppContainerProps) {
  const [uncontrolledActiveTab, setUncontrolledActiveTab] = useState<AppTabId>('preview')
  const activeTab = controlledActiveTab ?? uncontrolledActiveTab
  const setActiveTab = (id: AppTabId) => {
    // When controlled, defer entirely to the parent — don't mutate local
    // state so React doesn't keep two competing sources of truth around.
    if (controlledActiveTab === undefined) {
      setUncontrolledActiveTab(id)
    }
    onActiveTabChange?.(id)
  }

  // Per-tab telemetry strings — kept inline so the parent doesn't have to
  // know the recipe. The Preview line surfaces the hostname (sans scheme)
  // when available; everything else stays terse and counts-driven.
  const telemetry = computeTelemetry(activeTab, {
    previewHostname,
    runtimeState,
    tabCounts,
  })

  return (
    <Box
      sx={{
        height: '100%',
        width: '100%',
        display: 'flex',
        flexDirection: 'column',
        backgroundColor: appContainerTokens.canvasBg,
        overflow: 'hidden',
      }}
    >
      {/* Tab surfaces — ALL mounted, only the active one is visible. */}
      <Box sx={{ flex: 1, minHeight: 0, display: 'flex', flexDirection: 'column' }}>
        <PreviewTab
          projectId={projectId}
          branchId={branchId}
          previewHostname={previewHostname}
          runtimeState={runtimeState}
          active={activeTab === 'preview'}
          connection={connection}
        />
        <ChangesTab
          projectId={projectId}
          branchId={branchId}
          runtimeId={runtimeId}
          runtimeState={runtimeState}
          active={activeTab === 'changes'}
          connection={connection}
          currentBranch={currentBranch}
        />
        <SpecsTab projectId={projectId} active={activeTab === 'specs'} />
        <KanbanTab projectId={projectId} active={activeTab === 'kanban'} />
      </Box>

      {/* Bottom tab strip — suppressed on mobile where the outer 3-tab bar
          (Chat / Preview / Changes) supersedes this Preview / Changes one. */}
      {!hideTabStrip && (
        <Box
          component="nav"
          aria-label="Application tabs"
          sx={{
            flexShrink: 0,
            display: 'flex',
            alignItems: 'center',
            gap: 1.25,
            px: 1.5,
            py: 1,
            backgroundColor: appContainerTokens.chromeBg,
            borderTop: `1px solid ${appContainerTokens.hairline}`,
          }}
        >
          <SegmentedTabs
            value={activeTab}
            onChange={setActiveTab}
            items={TABS.map((t) => ({
              ...t,
              count: tabCounts?.[t.id],
            }))}
          />
          <Box sx={{ flex: 1 }} />
          {telemetry && (
            <Box
              component="span"
              sx={{
                fontFamily: appContainerTokens.fontMono,
                fontSize: '0.6875rem',
                color: appContainerTokens.textGhost,
                letterSpacing: '-0.005em',
                whiteSpace: 'nowrap',
                overflow: 'hidden',
                textOverflow: 'ellipsis',
                maxWidth: { xs: 0, sm: 280, md: 360 },
                // Hide entirely on very narrow viewports rather than crushing
                // the segmented pill — the active tab itself communicates
                // context, the telemetry is supplemental.
                display: { xs: 'none', sm: 'inline' },
              }}
            >
              {telemetry}
            </Box>
          )}
        </Box>
      )}
    </Box>
  )
}

// ── Tab catalogue ────────────────────────────────────────────────────────────

export type AppTabId = 'preview' | 'changes' | 'specs' | 'kanban'

interface AppTab {
  id: AppTabId
  label: string
  icon: ComponentType<SvgIconProps>
}

const TABS: readonly AppTab[] = [
  { id: 'preview', label: 'Preview', icon: PublicOutlinedIcon },
  { id: 'changes', label: 'Changes', icon: DifferenceOutlinedIcon },
  { id: 'specs', label: 'Specs', icon: ChecklistOutlinedIcon },
  { id: 'kanban', label: 'Kanban', icon: ViewKanbanOutlinedIcon },
] as const

// ── SegmentedTabs — pill-shaped recipe used in the bottom strip ──────────────

interface SegmentedTabsProps {
  value: AppTabId
  onChange: (next: AppTabId) => void
  items: ReadonlyArray<AppTab & { count?: number }>
}

/**
 * SegmentedTabs — a single chip-background container holding 24px-tall
 * tab buttons. The active one flips to a raised "surface" look with a
 * subtle shadow so the strip reads as a quiet macOS-style pill rather
 * than a flat OS chrome bar.
 *
 * <p>Each tab takes an optional {@code count} which renders as a small
 * tabular-nums mono badge to the right of the label. The badge sits on
 * the {@code chip} fill when the tab is active (so it carves out of the
 * surface) and is transparent when inactive.</p>
 */
function SegmentedTabs({ value, onChange, items }: SegmentedTabsProps) {
  return (
    <Box
      role="tablist"
      sx={{
        display: 'inline-flex',
        alignItems: 'center',
        gap: '2px',
        padding: '3px',
        borderRadius: '9px',
        backgroundColor: appContainerTokens.chipBg,
        border: `1px solid ${appContainerTokens.hairline}`,
      }}
    >
      {items.map((tab) => {
        const Icon = tab.icon
        const active = value === tab.id
        return (
          <Box
            key={tab.id}
            component="button"
            type="button"
            role="tab"
            aria-selected={active}
            onClick={() => onChange(tab.id)}
            sx={{
              display: 'inline-flex',
              alignItems: 'center',
              gap: '5px',
              height: 24,
              padding: '0 10px',
              borderRadius: '7px',
              border: 0,
              backgroundColor: active
                ? appContainerTokens.surface
                : 'transparent',
              color: active
                ? appContainerTokens.textPrimary
                : appContainerTokens.textMuted,
              fontWeight: active ? 600 : 500,
              fontSize: '0.71875rem', // 11.5px
              letterSpacing: '-0.005em',
              cursor: 'pointer',
              fontFamily: 'inherit',
              boxShadow: active ? appContainerTokens.shadowCardHover : 'none',
              transition:
                'background-color 120ms ease, color 120ms ease, box-shadow 120ms ease',
              '&:hover': active
                ? undefined
                : {
                    color: appContainerTokens.textPrimary,
                    backgroundColor: appContainerTokens.chipHoverBg,
                  },
              '&:focus-visible': {
                outline: `2px solid ${appContainerTokens.accent}`,
                outlineOffset: -2,
              },
            }}
          >
            <Icon
              sx={{
                fontSize: 12,
                color: 'inherit',
                opacity: active ? 1 : 0.85,
              }}
            />
            <Box component="span">{tab.label}</Box>
            {tab.count != null && (
              <Box
                component="span"
                sx={{
                  fontSize: '0.625rem', // 10px
                  fontWeight: 600,
                  color: active
                    ? appContainerTokens.textPrimary
                    : appContainerTokens.textFaint,
                  backgroundColor: active
                    ? appContainerTokens.chipBg
                    : 'transparent',
                  padding: '1px 5px',
                  borderRadius: 999,
                  fontFamily: appContainerTokens.fontMono,
                  fontVariantNumeric: 'tabular-nums',
                  marginLeft: '1px',
                  lineHeight: 1.4,
                }}
              >
                {tab.count}
              </Box>
            )}
          </Box>
        )
      })}
    </Box>
  )
}

// Marks the imported SVG type as used (defensive — keeps the symbol from
// being elided when the file is consumed via re-exports). The {@code void}
// expression has zero runtime impact.
void (null as unknown as SVGProps<SVGSVGElement>)

// ── Telemetry helper ─────────────────────────────────────────────────────────

interface TelemetryArgs {
  previewHostname: string | null
  runtimeState: RuntimeState | string | undefined
  tabCounts?: Partial<Record<AppTabId, number>>
}

/**
 * Per-tab right-side mono caption shown in the bottom strip. Kept terse
 * — the active tab already communicates context, this is the
 * supplemental "what state am I in" line.
 */
function computeTelemetry(
  active: AppTabId,
  { previewHostname, runtimeState, tabCounts }: TelemetryArgs,
): string | null {
  switch (active) {
    case 'preview':
      if (!previewHostname) return 'no preview'
      if (runtimeState === RuntimeState.Online) return previewHostname
      return `${previewHostname} · offline`
    case 'changes': {
      const n = tabCounts?.changes
      return n != null ? `${n} file${n === 1 ? '' : 's'} changed` : null
    }
    case 'specs': {
      const n = tabCounts?.specs
      return n != null ? `${n} spec${n === 1 ? '' : 's'}` : null
    }
    case 'kanban': {
      const n = tabCounts?.kanban
      return n != null ? `${n} card${n === 1 ? '' : 's'}` : null
    }
    default:
      return null
  }
}

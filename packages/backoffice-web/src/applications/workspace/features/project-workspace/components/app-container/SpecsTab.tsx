import { useMemo, useState } from 'react'
import { Box, Tooltip } from '@mui/material'
import ArrowBackIcon from '@mui/icons-material/ArrowBack'
import AddIcon from '@mui/icons-material/Add'
import {
  SpecDetailPage,
  SpecListPage,
} from '@/applications/super-admin/features/specifications'
import { useGetApiProjectsProjectIdSpecifications } from '@/api/queries-commands'
import { appContainerTokens } from './tokens'
import { SecondaryHeader } from './SecondaryHeader'

interface SpecsTabProps {
  /** Project the specs belong to. */
  projectId: string
  /** When false, the tab is hidden via {@code display: none} but stays mounted. */
  active: boolean
}

/**
 * Specs tab — embeds the project-scoped specification list and detail
 * surfaces inside the workspace AppContainer.
 *
 * <p>The tab owns the "which spec is open" toggle locally: <c>null</c>
 * shows the list, a non-null slug shows the detail view. Both child
 * pages accept their project id and the back / row-click callbacks via
 * props so they don't reach for <c>useParams</c> / <c>useNavigate</c>
 * (there's no inner router match at this layer).</p>
 *
 * <p>Chrome: renders a {@link SecondaryHeader} above the body so the
 * Specs tab gets the same "label · sub · right" recipe the Changes and
 * Kanban tabs use. The right slot promotes the "back to list" affordance
 * when a spec is open (replacing the inline back button on the embedded
 * detail page), and shows a disabled "New spec" stub on the list view —
 * matching the reference design's intent while signalling that the
 * agent (not the user) drafts specs through MCP.</p>
 *
 * <p>Like {@code PreviewTab} and {@code ChangesTab}, this stays mounted
 * at all times — the <c>active</c> prop toggles <c>display: none</c>
 * only, so flipping back to it preserves the user's selection and
 * scroll positions.</p>
 */
export function SpecsTab({ projectId, active }: SpecsTabProps) {
  const [selectedSlug, setSelectedSlug] = useState<string | null>(null)

  // Cheap piggy-back on the list query (already cached because the
  // SpecListPage below mounts it). We only read this for the sub-count
  // badge — keeping it scoped to the chrome and using the same query
  // key means there's no extra fetch.
  const listQuery = useGetApiProjectsProjectIdSpecifications(projectId, {
    query: { enabled: !!projectId },
  })

  const specsCount = listQuery.data?.length ?? null

  const sub = useMemo(() => {
    if (selectedSlug !== null) {
      // On a detail view the sub-text reads as a breadcrumb: which spec.
      return selectedSlug
    }
    if (specsCount === null) return null
    return `${specsCount} ${specsCount === 1 ? 'spec' : 'specs'}`
  }, [selectedSlug, specsCount])

  return (
    <Box
      sx={{
        flex: 1,
        minHeight: 0,
        display: active ? 'flex' : 'none',
        flexDirection: 'column',
        backgroundColor: appContainerTokens.canvasBg,
      }}
      aria-hidden={!active}
    >
      <SecondaryHeader
        label="Specifications"
        sub={sub}
        right={
          selectedSlug !== null ? (
            <BackToListButton onClick={() => setSelectedSlug(null)} />
          ) : (
            <NewSpecStub />
          )
        }
      />

      <Box sx={{ flex: 1, minHeight: 0, overflow: 'auto' }}>
        {selectedSlug === null ? (
          <SpecListPage
            projectId={projectId}
            onSelectSpec={setSelectedSlug}
            embedded
          />
        ) : (
          <SpecDetailPage
            projectId={projectId}
            slug={selectedSlug}
            onBack={() => setSelectedSlug(null)}
            embedded
          />
        )}
      </Box>
    </Box>
  )
}

/**
 * "← Back to list" chip rendered into the SecondaryHeader's right slot
 * when a spec is open. Matches the reference's ghostBtn recipe — chip
 * background, hairline border, muted text — but stays a real
 * {@code <button>} so keyboard nav + screen reader semantics hold.
 */
function BackToListButton({ onClick }: { onClick: () => void }) {
  return (
    <Box
      component="button"
      type="button"
      onClick={onClick}
      sx={{
        display: 'inline-flex',
        alignItems: 'center',
        gap: '5px',
        height: 26,
        px: '10px',
        border: `1px solid ${appContainerTokens.hairline}`,
        borderRadius: '7px',
        background: appContainerTokens.chipBg,
        color: appContainerTokens.textMuted,
        fontSize: '0.75rem', // 12px
        fontFamily: 'inherit',
        fontWeight: 500,
        letterSpacing: '-0.005em',
        cursor: 'pointer',
        transition: 'color 120ms ease, background-color 120ms ease, border-color 120ms ease',
        '&:hover': {
          color: appContainerTokens.textPrimary,
          backgroundColor: appContainerTokens.chipHoverBg,
          borderColor: appContainerTokens.accentBorder,
        },
        '&:focus-visible': {
          outline: `2px solid ${appContainerTokens.accent}`,
          outlineOffset: 1,
        },
      }}
    >
      <ArrowBackIcon sx={{ fontSize: 12 }} />
      <span>Back to list</span>
    </Box>
  )
}

/**
 * Disabled "New spec" stub. The reference design includes a primary
 * "+ New spec" button in the right slot, but in this product the agent
 * owns spec lifecycle through MCP — operators don't draft them by hand.
 * We keep the chip present for layout symmetry (mirrors the disabled
 * Share button on PreviewChrome) and signal intent via tooltip.
 */
function NewSpecStub() {
  return (
    <Tooltip title="The agent drafts specs — ask in chat" enterDelay={400}>
      <Box
        component="span"
        sx={{
          display: 'inline-flex',
          alignItems: 'center',
          gap: '5px',
          height: 26,
          px: '10px',
          border: `1px solid ${appContainerTokens.hairline}`,
          borderRadius: '7px',
          background: appContainerTokens.chipBg,
          color: appContainerTokens.textFaint,
          fontSize: '0.75rem',
          fontFamily: 'inherit',
          fontWeight: 500,
          letterSpacing: '-0.005em',
          cursor: 'default',
          userSelect: 'none',
        }}
      >
        <AddIcon sx={{ fontSize: 12 }} />
        <span>New spec</span>
      </Box>
    </Tooltip>
  )
}

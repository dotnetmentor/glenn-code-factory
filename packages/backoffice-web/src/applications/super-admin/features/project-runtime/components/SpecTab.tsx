import { useMemo, useState } from 'react'
import {
  Alert,
  Box,
  Button,
  CircularProgress,
  Stack,
  Tooltip,
  Typography,
} from '@mui/material'
import EditIcon from '@mui/icons-material/Edit'
import BookmarkAddOutlinedIcon from '@mui/icons-material/BookmarkAddOutlined'
import ReactDiffViewer, { DiffMethod } from 'react-diff-viewer-continued'
import {
  RuntimeProposalStatus,
  useGetApiProjectsProjectId,
  useGetApiProjectsProjectIdProposals,
  useGetApiProjectsProjectIdRuntimeSpec,
  type ProjectRuntimeSpecDto,
  type RuntimeProposalDto,
  type RuntimeSpecV3,
} from '@/api/queries-commands'
import {
  monoNumberSx,
  workspaceAccent,
  workspaceColors,
  workspaceFontFamily,
  workspaceText,
} from '@/applications/workspace/shared/designTokens'
import { ApplyHistorySection } from '@/applications/shared/runtime/components/ApplyHistorySection'
import {
  formatProposedSpec,
  formatRuntimeSpec,
} from '@/lib/format/formatProposedSpec'
import { SpecEditorDialog } from './SpecEditorDialog'
import { SaveAsCatalogSpecDialog } from './SaveAsCatalogSpecDialog'
import { PasteSpecDialog } from './PasteSpecDialog'

export interface SpecTabProps {
  projectId: string
  /**
   * Branch the runtime is pinned to. Required to fetch the branch-scoped
   * apply-history endpoint; when omitted the Apply history section is
   * suppressed (the rest of the tab keeps rendering off the project-scoped
   * spec + pending-proposal queries).
   */
  branchId?: string
}

/**
 * Spec tab of the runtime drawer (Runtime Spec V2, Phase 6 part 1).
 *
 * <p>Read-only surface that renders the project's currently-applied
 * {@link RuntimeSpecV3} as pretty-printed JSON. When a Pending proposal
 * exists, a side-by-side diff is rendered above the applied JSON so the
 * user can preview the change before approving it.</p>
 *
 * <p>Visually this tab matches the workspace surface — quiet text Edit
 * action, muted bronze/sage diff tints (no loud red/green), hairline
 * scroll container.</p>
 */
export function SpecTab({ projectId, branchId }: SpecTabProps) {
  const [editorOpen, setEditorOpen] = useState(false)
  const [saveCatalogOpen, setSaveCatalogOpen] = useState(false)
  const [pasteOpen, setPasteOpen] = useState(false)

  const specQuery = useGetApiProjectsProjectIdRuntimeSpec(projectId, {
    query: { enabled: !!projectId },
  })

  // The catalog endpoint is workspace-scoped, so we need the project's
  // workspace id to (a) wire it into the dialog for cache invalidation and
  // (b) confirm we're running in a workspace the user belongs to. The
  // project endpoint already exposes {@code workspaceId} on its DTO.
  const projectQuery = useGetApiProjectsProjectId(projectId, {
    query: { enabled: !!projectId },
  })
  const workspaceId = projectQuery.data?.workspaceId ?? ''

  const proposalsQuery = useGetApiProjectsProjectIdProposals(
    projectId,
    { status: RuntimeProposalStatus.Pending, limit: 5 },
    { query: { enabled: !!projectId } },
  )

  const appliedSpec = specQuery.data?.spec
  const appliedJson = useMemo(
    () => (appliedSpec ? formatRuntimeSpec(appliedSpec) : ''),
    [appliedSpec],
  )

  const pendingProposal = useMemo<RuntimeProposalDto | undefined>(() => {
    const list = proposalsQuery.data ?? []
    const pending = list.filter(
      (p) => p.status === RuntimeProposalStatus.Pending,
    )
    if (pending.length === 0) return undefined
    return [...pending].sort(
      (a, b) =>
        new Date(b.createdAt).getTime() - new Date(a.createdAt).getTime(),
    )[0]
  }, [proposalsQuery.data])

  const proposedJson = useMemo(() => {
    if (!pendingProposal) return ''
    return formatProposedSpec(pendingProposal.proposedSpec)
  }, [pendingProposal])

  const diffStats = useMemo(() => {
    if (!proposedJson || !appliedJson) return null
    return countLineDiff(appliedJson, proposedJson)
  }, [appliedJson, proposedJson])

  if (specQuery.isLoading) {
    return (
      <Stack
        direction="row"
        spacing={1.25}
        alignItems="center"
        sx={{ py: 4, justifyContent: 'center' }}
      >
        <CircularProgress size={16} sx={{ color: workspaceText.muted }} />
        <Typography variant="body2" sx={{ color: workspaceText.muted }}>
          Loading project spec…
        </Typography>
      </Stack>
    )
  }

  if (specQuery.isError) {
    return (
      <Alert severity="error">
        Failed to load the project spec. Try reopening the drawer.
      </Alert>
    )
  }

  const hasAppliedSpec = isMeaningfulSpec(appliedSpec)

  if (!hasAppliedSpec) {
    return (
      <Stack
        sx={{ flex: 1, alignItems: 'center', justifyContent: 'center', py: 6 }}
        spacing={2}
      >
        <Typography
          sx={{
            fontSize: '0.9375rem',
            fontWeight: 500,
            letterSpacing: '-0.005em',
            color: workspaceText.primary,
          }}
        >
          No spec configured for this project yet
        </Typography>
        <Typography
          sx={{
            fontSize: 13.5,
            color: workspaceText.muted,
            textAlign: 'center',
            maxWidth: 420,
            lineHeight: 1.55,
          }}
        >
          Propose one via the chat panel — the proposal will become the
          project's applied spec once approved.
        </Typography>
        <Typography
          sx={{
            fontSize: 12,
            color: workspaceText.faint,
            textTransform: 'uppercase',
            letterSpacing: '0.08em',
            fontWeight: 600,
          }}
        >
          or
        </Typography>
        <Button variant="quietOutlined" onClick={() => setPasteOpen(true)}>
          Paste spec JSON
        </Button>
        <PasteSpecDialog
          open={pasteOpen}
          onClose={() => setPasteOpen(false)}
          projectId={projectId}
        />
      </Stack>
    )
  }

  // The DTO's runtimeId is the most-recent live runtime, used as the SignalR
  // delta target when an edit / catalog-save mutates the project's spec. When
  // it's null, no runtimes are live to receive the delta, so Edit and Save-
  // to-Catalog are disabled until the user spins one up.
  const runtimeId = specQuery.data?.runtimeId ?? null
  const canSaveToCatalog = !!runtimeId && hasAppliedSpec

  return (
    <Stack spacing={2} sx={{ height: '100%', minHeight: 0 }}>
      <Toolbar
        appliedAt={specQuery.data?.specUpdatedAt}
        onEdit={() => setEditorOpen(true)}
        editDisabled={!runtimeId}
        onSaveToCatalog={() => setSaveCatalogOpen(true)}
        saveToCatalogDisabled={!canSaveToCatalog}
      />

      <Box sx={{ flex: 1, minHeight: 0, overflowY: 'auto', pr: 0.5 }}>
        {pendingProposal && (
          <DiffSection
            appliedJson={appliedJson}
            proposedJson={proposedJson}
            proposal={pendingProposal}
            changedLines={diffStats?.changed ?? 0}
          />
        )}

        <Stack spacing={1} sx={{ mb: 2.5 }}>
          <CaptionRow
            label={pendingProposal ? 'Applied spec' : 'Current applied spec'}
            meta={formatJsonSize(appliedJson)}
          />
          <CodeBlock>{appliedJson}</CodeBlock>
        </Stack>

        {branchId ? (
          <ApplyHistorySection
            projectId={projectId}
            branchId={branchId}
          />
        ) : (
          <Typography
            sx={{
              fontSize: 12.5,
              color: workspaceText.muted,
              mt: 1,
            }}
          >
            No apply history yet — open this panel from a branch tab to load
            history.
          </Typography>
        )}
      </Box>

      {runtimeId && appliedSpec && (
        <SpecEditorDialog
          open={editorOpen}
          onClose={() => setEditorOpen(false)}
          projectId={projectId}
          currentSpec={appliedSpec}
        />
      )}

      {runtimeId && (
        <SaveAsCatalogSpecDialog
          open={saveCatalogOpen}
          onClose={() => setSaveCatalogOpen(false)}
          runtimeId={runtimeId}
          workspaceId={workspaceId}
        />
      )}
    </Stack>
  )
}

interface ToolbarProps {
  appliedAt?: string | null
  onEdit: () => void
  editDisabled: boolean
  onSaveToCatalog: () => void
  saveToCatalogDisabled: boolean
}

function Toolbar({
  appliedAt,
  onEdit,
  editDisabled,
  onSaveToCatalog,
  saveToCatalogDisabled,
}: ToolbarProps) {
  const editButton = (
    <Button
      variant="quietOutlined"
      startIcon={<EditIcon sx={{ fontSize: 14 }} />}
      onClick={onEdit}
      disabled={editDisabled}    >
      Edit
    </Button>
  )
  const saveToCatalogButton = (
    <Button
      variant="quietOutlined"
      startIcon={<BookmarkAddOutlinedIcon sx={{ fontSize: 14 }} />}
      onClick={onSaveToCatalog}
      disabled={saveToCatalogDisabled}    >
      Save as catalog spec
    </Button>
  )
  return (
    <Stack
      direction="row"
      alignItems="baseline"
      spacing={1}
      sx={{ flexShrink: 0 }}
    >
      <Typography
        sx={{
          fontSize: '0.9375rem',
          fontWeight: 600,
          letterSpacing: '-0.005em',
          color: workspaceText.primary,
        }}
      >
        Project spec
      </Typography>
      {appliedAt && (
        <Typography
          sx={{ fontSize: 11.5, color: workspaceText.faint }}
        >
          · Applied {formatTimestamp(appliedAt)}
        </Typography>
      )}
      <Box sx={{ flex: 1 }} />
      {saveToCatalogDisabled ? (
        <Tooltip title="Spin up a runtime to save the project's spec to the catalog.">
          <span>{saveToCatalogButton}</span>
        </Tooltip>
      ) : (
        saveToCatalogButton
      )}
      {editDisabled ? (
        <Tooltip title="Spin up a runtime to edit the spec.">
          <span>{editButton}</span>
        </Tooltip>
      ) : (
        editButton
      )}
    </Stack>
  )
}

interface DiffSectionProps {
  appliedJson: string
  proposedJson: string
  proposal: RuntimeProposalDto
  changedLines: number
}

function DiffSection({
  appliedJson,
  proposedJson,
  proposal,
  changedLines,
}: DiffSectionProps) {
  const sourceLabel = formatProposalSource(proposal.decidedBy)
  return (
    <Section
      title={
        <Stack direction="row" spacing={0.75} alignItems="baseline">
          <Box component="span">Pending proposal</Box>
          {changedLines > 0 && (
            <Box
              component="span"
              sx={{
                fontFamily: workspaceFontFamily.mono,
                fontSize: '0.6875rem',
                color: workspaceText.muted,
                letterSpacing: 0,
                textTransform: 'none',
                fontWeight: 500,
              }}
            >
              · {changedLines} change{changedLines === 1 ? '' : 's'}
            </Box>
          )}
        </Stack>
      }
      subtitle={
        <Stack
          direction="row"
          spacing={0.75}
          alignItems="center"
          flexWrap="wrap"
          useFlexGap
        >
          <Box
            sx={{
              fontSize: '0.6875rem',
              fontWeight: 600,
              letterSpacing: '0.04em',
              textTransform: 'uppercase',
              color: workspaceAccent.ink,
              bgcolor: workspaceAccent.surface,
              border: `1px solid ${workspaceAccent.border}`,
              borderRadius: 999,
              px: 0.875,
              py: 0.125,
            }}
          >
            Pending
          </Box>
          {sourceLabel && (
            <Typography
              sx={{ fontSize: 12, color: workspaceText.muted }}
            >
              from {sourceLabel}
            </Typography>
          )}
          {proposal.createdAt && (
            <Typography sx={{ fontSize: 12, color: workspaceText.faint }}>
              · {formatTimestamp(proposal.createdAt)}
            </Typography>
          )}
        </Stack>
      }
    >
      <Box
        sx={{
          border: `1px solid ${workspaceColors.codeBorder}`,
          borderRadius: 1,
          overflow: 'hidden',
          backgroundColor: workspaceColors.codeBg,
          '& pre, & td.diff-cell, & div': {
            fontFamily: workspaceFontFamily.mono,
          },
          fontSize: 12.5,
        }}
      >
        <ReactDiffViewer
          oldValue={appliedJson}
          newValue={proposedJson}
          splitView
          compareMethod={DiffMethod.LINES}
          leftTitle="Currently applied"
          rightTitle="Pending proposal"
          useDarkTheme={false}
          styles={MUTED_DIFF_STYLES}
        />
      </Box>
    </Section>
  )
}


interface SectionProps {
  title: React.ReactNode
  subtitle?: React.ReactNode
  children: React.ReactNode
}

function Section({ title, subtitle, children }: SectionProps) {
  return (
    <Stack spacing={1} sx={{ mb: 2.5 }}>
      <Box>
        <Typography
          component="div"
          sx={{
            fontSize: '0.6875rem',
            fontWeight: 600,
            letterSpacing: '0.08em',
            textTransform: 'uppercase',
            color: workspaceText.muted,
          }}
        >
          {title}
        </Typography>
        {typeof subtitle === 'string' ? (
          <Typography
            sx={{
              fontSize: 12,
              color: workspaceText.faint,
              display: 'block',
              mt: 0.25,
            }}
          >
            {subtitle}
          </Typography>
        ) : subtitle ? (
          <Box sx={{ mt: 0.5 }}>{subtitle}</Box>
        ) : null}
      </Box>
      {children}
    </Stack>
  )
}

/**
 * Uppercase caption row used above the applied-spec code panel — mirrors the
 * prototype's "CURRENT APPLIED SPEC" row: an uppercase label, a hairline rule
 * that fills the remaining width, and a right-aligned mono meta chip (the JSON
 * byte size).
 */
function CaptionRow({ label, meta }: { label: string; meta?: string }) {
  return (
    <Stack direction="row" alignItems="center" spacing={1}>
      <Typography
        component="span"
        sx={{
          fontSize: '0.6875rem',
          fontWeight: 600,
          letterSpacing: '0.06em',
          textTransform: 'uppercase',
          color: workspaceText.faint,
          whiteSpace: 'nowrap',
        }}
      >
        {label}
      </Typography>
      <Box
        sx={{ flex: 1, height: '1px', backgroundColor: workspaceColors.hairline }}
      />
      {meta && (
        <Box
          component="span"
          sx={{
            ...monoNumberSx,
            fontSize: '0.65625rem',
            color: workspaceText.ghost,
            whiteSpace: 'nowrap',
          }}
        >
          {meta}
        </Box>
      )}
    </Stack>
  )
}

function CodeBlock({ children }: { children: string }) {
  return (
    <Box
      component="pre"
      sx={{
        m: 0,
        p: '12px 14px',
        bgcolor: workspaceColors.codeBg,
        color: workspaceText.primary,
        border: `1px solid ${workspaceColors.codeBorder}`,
        borderRadius: 1,
        fontFamily: workspaceFontFamily.mono,
        fontSize: 12.5,
        lineHeight: 1.65,
        whiteSpace: 'pre-wrap',
        wordBreak: 'break-word',
        maxHeight: 480,
        overflowY: 'auto',
      }}
    >
      {children}
    </Box>
  )
}

/**
 * Renders the byte size of the applied-spec JSON as a "JSON · 1.4 KB" meta
 * string for the caption row. Mirrors the prototype's right-aligned size hint.
 */
function formatJsonSize(json: string): string | undefined {
  if (!json) return undefined
  const bytes =
    typeof TextEncoder !== 'undefined'
      ? new TextEncoder().encode(json).length
      : json.length
  if (bytes < 1024) return `JSON · ${bytes} B`
  return `JSON · ${(bytes / 1024).toFixed(1)} KB`
}

function countLineDiff(a: string, b: string): { changed: number } {
  const aLines = a.split('\n')
  const bLines = b.split('\n')
  const max = Math.max(aLines.length, bLines.length)
  let changed = 0
  for (let i = 0; i < max; i++) {
    if (aLines[i] !== bLines[i]) changed++
  }
  return { changed }
}

function isMeaningfulSpec(spec: RuntimeSpecV3 | undefined): boolean {
  if (!spec) return false
  const hasInstall = !!spec.install && spec.install.trim().length > 0
  const hasSetup = !!spec.setup && spec.setup.trim().length > 0
  const hasServices = !!spec.services && spec.services.length > 0
  return hasInstall || hasSetup || hasServices
}

function formatTimestamp(value: string | null | undefined): string {
  if (!value) return ''
  try {
    return new Date(value).toLocaleString()
  } catch {
    return value
  }
}

function formatProposalSource(decidedBy: string | null | undefined): string {
  if (decidedBy && decidedBy.trim().length > 0) return decidedBy
  return 'AI'
}

/**
 * Muted palette for the diff viewer — matches the SpecEditorDialog confirm
 * step so both surfaces feel like the same workspace artefact.
 */
const MUTED_DIFF_STYLES = {
  variables: {
    light: {
      diffViewerBackground: workspaceColors.codeBg,
      diffViewerColor: workspaceText.primary,
      addedBackground: 'rgba(127, 178, 87, 0.08)',
      addedColor: workspaceText.primary,
      removedBackground: 'rgba(178, 84, 56, 0.06)',
      removedColor: workspaceText.primary,
      wordAddedBackground: 'rgba(127, 178, 87, 0.22)',
      wordRemovedBackground: 'rgba(178, 84, 56, 0.18)',
      addedGutterBackground: 'rgba(127, 178, 87, 0.12)',
      removedGutterBackground: 'rgba(178, 84, 56, 0.08)',
      gutterBackground: workspaceColors.chipBg,
      gutterBackgroundDark: workspaceColors.chromeBg,
      highlightBackground: workspaceColors.chipHoverBg,
      highlightGutterBackground: workspaceColors.chipHoverBg,
      codeFoldGutterBackground: workspaceColors.chromeBg,
      codeFoldBackground: workspaceColors.chromeBg,
      emptyLineBackground: 'transparent',
      gutterColor: workspaceText.faint,
      addedGutterColor: workspaceText.muted,
      removedGutterColor: workspaceText.muted,
      codeFoldContentColor: workspaceText.muted,
      diffViewerTitleBackground: workspaceColors.chromeBg,
      diffViewerTitleColor: workspaceText.muted,
      diffViewerTitleBorderColor: workspaceColors.hairline,
    },
  },
  contentText: {
    fontFamily: workspaceFontFamily.mono,
  },
} as const

export type { ProjectRuntimeSpecDto }

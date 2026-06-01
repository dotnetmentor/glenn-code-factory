import {
  Component,
  Suspense,
  lazy,
  useMemo,
  useState,
  type ErrorInfo,
  type ReactNode,
} from 'react'
import { Box, Stack, Tooltip, Typography } from '@mui/material'
import { appContainerTokens } from '../tokens'
import { LoadingListState, NoSelectionState } from './EmptyStates'
import type { FileDiffResponse } from '../../../../../../../api/queries-commands'

/**
 * Lazy chunk for {@code @git-diff-view/react}. The Preview tab — the
 * only other surface in the AppContainer — should not pay the bundle
 * cost of the diff renderer until the user actually opens the Changes
 * tab and selects a file.
 *
 * <p>The library exports {@code DiffView} as a named binding plus a
 * {@code DiffModeEnum} we use to pick split vs unified at runtime.
 * {@code React.lazy} wants a default export, so we adapt the module
 * shape here.</p>
 */
const LazyDiffView = lazy(async () => {
  const [mod] = await Promise.all([
    import('@git-diff-view/react'),
    // Stylesheet ships separately so the renderer can theme via CSS
    // variables without forcing every consumer to pre-import it. Bundled
    // into the same chunk as the renderer so we only pay for it once,
    // and only after the user actually opens the Changes tab.
    import('@git-diff-view/react/styles/diff-view.css'),
  ])
  // We also need the runtime mode enum, but lazy() only carries the
  // default export — stash the enum on a module-scope handle so the
  // render path can read it synchronously by the time it's needed.
  diffModeEnumRef.value = mod.DiffModeEnum
  return { default: mod.DiffView }
})

// Side-channel for the {@link DiffModeEnum} — populated by the lazy
// import above the first time the chunk loads. The renderer only reads
// from this after Suspense has resolved, so it's never null at use.
const diffModeEnumRef: { value: { Split: number; Unified: number } | null } = {
  value: null,
}

interface DiffViewerProps {
  /** The selected file's path, or null when nothing is selected. */
  selectedPath: string | null
  /** Latest file-diff response, undefined while loading or unselected. */
  data: FileDiffResponse | undefined
  isLoading: boolean
  isError: boolean
}

type DiffMode = 'unified' | 'split'

/**
 * Right-pane diff viewer. Wraps the off-the-shelf
 * {@code @git-diff-view/react} renderer with:
 * <ul>
 *   <li>A 36px sub-bar holding the path on the left and a {@code
 *       unified | split} toggle on the right (per-file concern, not
 *       chrome-level).</li>
 *   <li>An error boundary that drops back to a {@code <pre>} of the raw
 *       unified diff, so the user always sees <em>something</em> even if
 *       the renderer throws.</li>
 *   <li>Deliberately calm empty / loading / binary / truncated states
 *       — the contract is "the diff is the document; everything else is
 *       a margin note".</li>
 * </ul>
 */
export function DiffViewer({
  selectedPath,
  data,
  isLoading,
  isError,
}: DiffViewerProps) {
  const [mode, setMode] = useState<DiffMode>('unified')

  if (!selectedPath) {
    return (
      <ViewerShell>
        <NoSelectionState />
      </ViewerShell>
    )
  }

  if (isLoading && !data) {
    return (
      <ViewerShell path={selectedPath} mode={mode} onModeChange={setMode}>
        <LoadingListState />
      </ViewerShell>
    )
  }

  if (isError) {
    return (
      <ViewerShell path={selectedPath} mode={mode} onModeChange={setMode}>
        <CalmMessage
          title="Couldn't load diff"
          body="The runtime didn't return a diff for this file. Try Refresh on the chrome above."
        />
      </ViewerShell>
    )
  }

  if (!data) {
    return (
      <ViewerShell path={selectedPath} mode={mode} onModeChange={setMode}>
        <LoadingListState />
      </ViewerShell>
    )
  }

  if (data.isBinary) {
    return (
      <ViewerShell path={selectedPath} mode={mode} onModeChange={setMode}>
        <CalmMessage
          title="Binary file"
          body={
            data.reason === 'submodule'
              ? 'Submodule pointer changed.'
              : 'No diff is rendered for binary files.'
          }
        />
      </ViewerShell>
    )
  }

  const unifiedDiff = data.unifiedDiff ?? ''
  if (!unifiedDiff.trim()) {
    return (
      <ViewerShell path={selectedPath} mode={mode} onModeChange={setMode}>
        <CalmMessage
          title="No textual changes"
          body="The file's contents are unchanged on this side of the diff."
        />
      </ViewerShell>
    )
  }

  return (
    <ViewerShell path={selectedPath} mode={mode} onModeChange={setMode}>
      {data.isTruncated && (
        <Box
          sx={{
            px: 1.5,
            py: 0.75,
            borderBottom: `1px solid ${appContainerTokens.hairline}`,
            backgroundColor: appContainerTokens.accentFaint,
          }}
        >
          <Typography
            variant="caption"
            sx={{
              color: appContainerTokens.textMuted,
              fontSize: '0.75rem',
              letterSpacing: '-0.005em',
            }}
          >
            Diff was clipped at the per-file size limit. Showing the head
            of the file only.
          </Typography>
        </Box>
      )}
      <Box sx={{ flex: 1, minHeight: 0, overflow: 'auto' }}>
        <DiffViewerErrorBoundary fallback={unifiedDiff}>
          <Suspense fallback={<LoadingListState />}>
            <DiffRenderer
              path={selectedPath}
              unifiedDiff={unifiedDiff}
              mode={mode}
            />
          </Suspense>
        </DiffViewerErrorBoundary>
      </Box>
    </ViewerShell>
  )
}

interface ViewerShellProps {
  path?: string
  mode?: DiffMode
  onModeChange?: (next: DiffMode) => void
  children: ReactNode
}

function ViewerShell({ path, mode, onModeChange, children }: ViewerShellProps) {
  return (
    <Box
      role="region"
      aria-label={path ? `Diff for ${path}` : 'Diff viewer'}
      sx={{
        flex: 1,
        minWidth: 0,
        minHeight: 0,
        display: 'flex',
        flexDirection: 'column',
        backgroundColor: appContainerTokens.canvasBg,
      }}
    >
      {path && mode && onModeChange && (
        <DiffSubBar path={path} mode={mode} onModeChange={onModeChange} />
      )}
      <Box
        sx={{
          flex: 1,
          minHeight: 0,
          display: 'flex',
          flexDirection: 'column',
          minWidth: 0,
          overflow: 'hidden',
        }}
      >
        {children}
      </Box>
    </Box>
  )
}

interface DiffSubBarProps {
  path: string
  mode: DiffMode
  onModeChange: (next: DiffMode) => void
}

function DiffSubBar({ path, mode, onModeChange }: DiffSubBarProps) {
  return (
    <Box
      sx={{
        height: 36,
        flexShrink: 0,
        px: 1.5,
        display: 'flex',
        alignItems: 'center',
        gap: 1,
        backgroundColor: appContainerTokens.chromeBg,
        borderBottom: `1px solid ${appContainerTokens.hairline}`,
      }}
    >
      <Tooltip title={path} enterDelay={500}>
        <Typography
          variant="body2"
          sx={{
            flex: 1,
            minWidth: 0,
            color: appContainerTokens.textMuted,
            fontSize: '0.75rem',
            letterSpacing: '-0.005em',
            fontFamily:
              'ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, "Liberation Mono", "Courier New", monospace',
            whiteSpace: 'nowrap',
            overflow: 'hidden',
            textOverflow: 'ellipsis',
            direction: 'rtl',
            textAlign: 'left',
          }}
        >
          <bdi>{path}</bdi>
        </Typography>
      </Tooltip>
      <Stack
        direction="row"
        sx={{
          height: 24,
          borderRadius: '12px',
          backgroundColor: 'rgba(0, 0, 0, 0.03)',
          border: `1px solid ${appContainerTokens.hairline}`,
          overflow: 'hidden',
        }}
        role="group"
        aria-label="Diff layout"
      >
        <ModeToggleButton
          active={mode === 'unified'}
          label="Unified"
          onClick={() => onModeChange('unified')}
        />
        <ModeToggleButton
          active={mode === 'split'}
          label="Split"
          onClick={() => onModeChange('split')}
        />
      </Stack>
    </Box>
  )
}

interface ModeToggleButtonProps {
  active: boolean
  label: string
  onClick: () => void
}

function ModeToggleButton({ active, label, onClick }: ModeToggleButtonProps) {
  return (
    <Box
      component="button"
      type="button"
      onClick={onClick}
      aria-pressed={active}
      sx={{
        px: 1.25,
        height: '100%',
        display: 'inline-flex',
        alignItems: 'center',
        justifyContent: 'center',
        border: 0,
        background: active ? appContainerTokens.accentSurface : 'transparent',
        color: active ? appContainerTokens.accent : appContainerTokens.textMuted,
        fontSize: '0.7rem',
        letterSpacing: 0,
        cursor: 'pointer',
        transition: 'color 200ms ease, background-color 200ms ease',
        '&:hover': {
          color: appContainerTokens.accent,
          backgroundColor: appContainerTokens.accentMuted,
        },
        '&:focus-visible': {
          outline: `2px solid ${appContainerTokens.accent}`,
          outlineOffset: -2,
        },
      }}
    >
      {label}
    </Box>
  )
}

interface DiffRendererProps {
  path: string
  unifiedDiff: string
  mode: DiffMode
}

/**
 * The renderer is the actual {@code <DiffView>} from
 * {@code @git-diff-view/react}. The library wants the diff body split
 * into "hunks" — for our purposes the entire unified-diff payload from
 * the backend is one hunk, which the library happily parses.
 */
function DiffRenderer({ path, unifiedDiff, mode }: DiffRendererProps) {
  const data = useMemo(
    () => ({
      newFile: { fileName: path, content: '' },
      oldFile: { fileName: path, content: '' },
      hunks: [unifiedDiff],
    }),
    [path, unifiedDiff],
  )
  const enumValues = diffModeEnumRef.value
  // Fallback to the wire-known constants in case the lazy chunk loaded
  // through some path that didn't run our bookkeeping. Per the package
  // sources: Split = 3, Unified = 4.
  const splitMode = enumValues?.Split ?? 3
  const unifiedMode = enumValues?.Unified ?? 4
  return (
    <LazyDiffView
      data={data}
      diffViewMode={mode === 'split' ? splitMode : unifiedMode}
      diffViewWrap={false}
      diffViewTheme="light"
      diffViewFontSize={12}
      diffViewHighlight={false}
    />
  )
}

interface CalmMessageProps {
  title: string
  body: string
}

function CalmMessage({ title, body }: CalmMessageProps) {
  return (
    <Stack
      spacing={1}
      sx={{ alignSelf: 'center', textAlign: 'center', px: 4, m: 'auto', maxWidth: 420 }}
    >
      <Typography
        variant="body1"
        sx={{
          color: appContainerTokens.textPrimary,
          fontWeight: 500,
          letterSpacing: '-0.005em',
        }}
      >
        {title}
      </Typography>
      <Typography
        variant="body2"
        sx={{
          color: appContainerTokens.textMuted,
          letterSpacing: '-0.005em',
        }}
      >
        {body}
      </Typography>
    </Stack>
  )
}

interface DiffViewerErrorBoundaryProps {
  fallback: string
  children: ReactNode
}

interface DiffViewerErrorBoundaryState {
  hasError: boolean
}

/**
 * Tight error boundary so a renderer crash never blanks out the tab.
 * On error, we render the raw unified-diff text in a {@code <pre>} —
 * the user always sees something legible.
 */
class DiffViewerErrorBoundary extends Component<
  DiffViewerErrorBoundaryProps,
  DiffViewerErrorBoundaryState
> {
  constructor(props: DiffViewerErrorBoundaryProps) {
    super(props)
    this.state = { hasError: false }
  }

  static getDerivedStateFromError(): DiffViewerErrorBoundaryState {
    return { hasError: true }
  }

  componentDidCatch(error: Error, info: ErrorInfo): void {
    // eslint-disable-next-line no-console
    console.error('[DiffViewer] renderer threw:', error, info)
  }

  render(): ReactNode {
    if (this.state.hasError) {
      return (
        <Box
          component="pre"
          sx={{
            m: 0,
            p: 1.5,
            fontFamily:
              'ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, "Liberation Mono", "Courier New", monospace',
            fontSize: '0.75rem',
            color: appContainerTokens.textPrimary,
            whiteSpace: 'pre',
            overflow: 'auto',
          }}
        >
          {this.props.fallback}
        </Box>
      )
    }
    return this.props.children
  }
}

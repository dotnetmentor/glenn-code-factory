import { useRef, useState } from 'react'
import { Box } from '@mui/material'
import {
  RuntimeState,
  type ChangedFilesResponse,
} from '../../../../../../../api/queries-commands'
import type { AgentHubConnection } from '../../../../../../../lib/signalr'
import { appContainerTokens } from '../tokens'
import { useResizableSplit } from '../useResizableSplit'
import { ChangesChrome } from './ChangesChrome'
import { FileList } from './FileList'
import { DiffViewer } from './DiffViewer'
import {
  ErrorState,
  LoadingListState,
  NoChangesState,
  NoSelectionState,
  RuntimeOfflineState,
} from './EmptyStates'
import { useChangedFiles } from './useChangedFiles'
import { useFileDiff } from './useFileDiff'
import type { CompareScope } from './types'
import { DEFAULT_COMPARE_SCOPE } from './types'

interface ChangesTabProps {
  projectId: string
  branchId: string
  /**
   * The active branch's runtime id. Diff endpoints are runtime-scoped
   * — the daemon for that runtime is what reads the working tree.
   * When empty (between project loads) the tab renders the offline
   * empty state.
   */
  runtimeId: string
  runtimeState: RuntimeState | string | undefined
  /** When false, the tab is hidden via {@code display: none}. */
  active: boolean
  /**
   * Shared AgentHub connection — kept on the prop surface for
   * Phase 2's {@code WorkingTreeChanged} subscription. Phase 1
   * doesn't read from it.
   */
  // eslint-disable-next-line @typescript-eslint/no-unused-vars
  connection: AgentHubConnection | null
  /**
   * Name of the branch the user is currently on. Threaded into the
   * {@link CompareAgainstPicker} so it can render the
   * "you're on the base branch" tooltip.
   */
  currentBranch?: string
}

/**
 * The Changes tab shell.
 *
 * <p>Layout, top-to-bottom:
 * <ul>
 *   <li>{@link ChangesChrome} — 44px refresh + scope pill + stats.</li>
 *   <li>A horizontal split between the file list (left) and the diff
 *       viewer (right), backed by {@link useResizableSplit} so the
 *       ratio persists per project + branch in {@code localStorage}.</li>
 * </ul></p>
 *
 * <p>The tab stays mounted at all times — switching to Preview
 * toggles {@code display: none} only — so the file selection and any
 * scroll positions survive a tab toggle (matching PreviewTab's
 * contract).</p>
 */
export function ChangesTab({
  projectId,
  branchId,
  runtimeId,
  runtimeState,
  active,
  connection: _connection,
  currentBranch,
}: ChangesTabProps) {
  // Default is branch-vs-main, NOT working-tree — the harness auto-commits
  // every turn so the working tree is almost always empty. The picker
  // promotes other scopes from here.
  /**
   * When the user lands on the base branch, branch-vs-base would be a
   * self-compare (always empty by definition). Fall back to working-tree
   * so the tab shows something useful; the picker still lets them switch.
   */
  const [scope, setScope] = useState<CompareScope>(() => {
    const defaultBase =
      DEFAULT_COMPARE_SCOPE.kind === 'branch'
        ? DEFAULT_COMPARE_SCOPE.base
        : undefined
    return currentBranch && currentBranch === defaultBase
      ? { kind: 'workingTree' }
      : DEFAULT_COMPARE_SCOPE
  })
  const [selectedPath, setSelectedPath] = useState<string | null>(null)

  const isOnline = runtimeState === RuntimeState.Online

  const changedFiles = useChangedFiles({
    runtimeId,
    scope,
    enabled: active,
    runtimeState,
  })

  const fileDiff = useFileDiff({
    runtimeId,
    scope,
    path: selectedPath,
    enabled: active,
  })

  const splitContainerRef = useRef<HTMLDivElement | null>(null)
  const { chatFraction: leftFraction, onResizeStart, isResizing } =
    useResizableSplit({
      storageKey: `diff-split-${projectId}-${branchId}`,
      defaultFraction: 0.4,
      minFraction: 0.2,
      maxFraction: 0.7,
      containerRef: splitContainerRef,
    })

  const handleRefresh = () => {
    changedFiles.refetch()
    if (selectedPath) {
      fileDiff.refetch()
    }
  }

  const data: ChangedFilesResponse | undefined = changedFiles.data
  const hasNoFiles =
    !!data && data.files.length === 0 && !changedFiles.isLoading
  const errored = changedFiles.isError && !data
  // Once the user has selected a row we keep showing the split layout
  // even if the working tree later goes empty — the empty state would
  // yank the diff out from under them, which is exactly what the spec
  // warns against. The user clicks Refresh to recover.
  const showEmptyHero =
    !isOnline ||
    (hasNoFiles && !selectedPath) ||
    (changedFiles.isLoading && !data) ||
    errored

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
      <ChangesChrome
        fileCount={data ? data.files.length : null}
        totalAdditions={data ? data.totalAdditions : null}
        totalDeletions={data ? data.totalDeletions : null}
        onRefresh={handleRefresh}
        busy={changedFiles.isFetching}
        disabled={!isOnline}
        scope={scope}
        onScopeChange={(next) => {
          setScope(next)
          // Clearing the selection avoids the diff pane fetching a path
          // that no longer exists in the new scope.
          setSelectedPath(null)
        }}
        projectId={projectId}
        runtimeId={runtimeId}
        currentBranch={currentBranch}
      />

      {showEmptyHero ? (
        <Box
          sx={{
            flex: 1,
            minHeight: 0,
            display: 'flex',
            alignItems: 'center',
            justifyContent: 'center',
          }}
        >
          {!isOnline && <RuntimeOfflineState />}
          {isOnline && changedFiles.isLoading && !data && <LoadingListState />}
          {isOnline && errored && <ErrorState />}
          {isOnline && !errored && hasNoFiles && (
            <NoChangesState scope={scope} currentBranch={currentBranch} />
          )}
        </Box>
      ) : (
        <Box
          ref={splitContainerRef}
          sx={{
            flex: 1,
            minHeight: 0,
            display: 'flex',
            flexDirection: 'row',
            position: 'relative',
          }}
        >
          <Box
            sx={{
              flex: `${leftFraction} 1 0`,
              minWidth: 0,
              display: 'flex',
              flexDirection: 'column',
              borderRight: `1px solid ${appContainerTokens.hairline}`,
            }}
          >
            {data && data.files.length > 0 ? (
              <FileList
                data={data}
                selectedPath={selectedPath}
                onSelect={setSelectedPath}
              />
            ) : (
              <Box
                sx={{
                  flex: 1,
                  minHeight: 0,
                  display: 'flex',
                  alignItems: 'center',
                  justifyContent: 'center',
                }}
              >
                {changedFiles.isLoading ? (
                  <LoadingListState />
                ) : (
                  <NoChangesState scope={scope} currentBranch={currentBranch} />
                )}
              </Box>
            )}
          </Box>
          <SplitHandle onMouseDown={onResizeStart} isResizing={isResizing} />
          <Box
            sx={{
              flex: `${1 - leftFraction} 1 0`,
              minWidth: 0,
              display: 'flex',
              flexDirection: 'column',
            }}
          >
            {selectedPath ? (
              <DiffViewer
                selectedPath={selectedPath}
                data={fileDiff.data}
                isLoading={fileDiff.isLoading}
                isError={fileDiff.isError}
              />
            ) : (
              <Box
                sx={{
                  flex: 1,
                  minHeight: 0,
                  display: 'flex',
                  alignItems: 'center',
                  justifyContent: 'center',
                }}
              >
                <NoSelectionState />
              </Box>
            )}
          </Box>
        </Box>
      )}
    </Box>
  )
}

interface SplitHandleProps {
  onMouseDown: (event: React.MouseEvent) => void
  isResizing: boolean
}

/**
 * Vertical drag handle between the file list and the diff viewer.
 * Same idiom as the outer chat/AppContainer split — narrow, hairline
 * borders, tints to a low-opacity accent on hover/active so the
 * affordance reads as a seam rather than a control.
 */
function SplitHandle({ onMouseDown, isResizing }: SplitHandleProps) {
  return (
    <Box
      role="separator"
      aria-orientation="vertical"
      onMouseDown={onMouseDown}
      sx={{
        flexShrink: 0,
        width: 5,
        cursor: 'col-resize',
        backgroundColor: isResizing ? appContainerTokens.accentActive : 'transparent',
        transition: 'background-color 200ms ease',
        '&:hover': {
          backgroundColor: appContainerTokens.accentSurface,
        },
      }}
    />
  )
}

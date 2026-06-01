import { Box } from '@mui/material'
import { appContainerTokens } from '../tokens'
import { FileRow } from './FileRow'
import { TruncatedFooter } from './EmptyStates'
import type { ChangedFilesResponse } from '../../../../../../../api/queries-commands'

interface FileListProps {
  data: ChangedFilesResponse
  selectedPath: string | null
  onSelect: (path: string) => void
}

/**
 * Plain-mapped, scrollable list of changed files.
 *
 * <p>Phase 1 ships unvirtualised — virtualisation is a Phase-out
 * concern (the spec calls it out in Phase 2.8 / 5). For working-tree
 * sized changesets (tens, occasionally hundreds of files) the simple
 * map performs well; the daemon caps the response at 5,000 files
 * regardless and renders the {@link TruncatedFooter} when it does.</p>
 */
export function FileList({ data, selectedPath, onSelect }: FileListProps) {
  // The daemon sets {@code reason='too-many'} when it truncated the file
  // list at the 5,000-file cap. We surface that as a calm footer rather
  // than an error — the diff view of the visible files is still useful.
  const totalCount = data.files.length
  const truncated = data.reason === 'too-many'

  return (
    <Box
      sx={{
        flex: 1,
        minHeight: 0,
        display: 'flex',
        flexDirection: 'column',
        backgroundColor: appContainerTokens.canvasBg,
      }}
    >
      <Box
        sx={{
          flex: 1,
          minHeight: 0,
          overflow: 'auto',
        }}
      >
        {data.files.map((file) => (
          <FileRow
            key={`${file.status}:${file.oldPath ?? ''}:${file.path}`}
            file={file}
            selected={selectedPath === file.path}
            onSelect={onSelect}
          />
        ))}
      </Box>
      {truncated && (
        <TruncatedFooter shown={totalCount} total={totalCount} />
      )}
    </Box>
  )
}

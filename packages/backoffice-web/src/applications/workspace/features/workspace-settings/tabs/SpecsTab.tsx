import { useMemo, useState } from 'react'
import {
  Alert,
  Box,
  Button,
  IconButton,
  Menu,
  MenuItem,
  Skeleton,
  Stack,
  Table,
  TableBody,
  TableCell,
  TableContainer,
  TableRow,
  Tooltip,
  Typography,
} from '@mui/material'
import AddIcon from '@mui/icons-material/Add'
import ContentCopyOutlinedIcon from '@mui/icons-material/ContentCopyOutlined'
import DeleteOutlineIcon from '@mui/icons-material/DeleteOutline'
import EditOutlinedIcon from '@mui/icons-material/EditOutlined'
import MoreHorizIcon from '@mui/icons-material/MoreHoriz'
import { formatDistanceToNow, parseISO } from 'date-fns'
import { useGetApiWorkspacesWorkspaceIdSpecs } from '../../../../../api/queries-commands'
import type { WorkspaceSpecListItem } from '../../../../../api/queries-commands'
import { useWorkspace } from '../../../../shared/contexts/WorkspaceContext'
import { DeleteSpecDialog } from '../components/DeleteSpecDialog'
import { DuplicateSpecDialog } from '../components/DuplicateSpecDialog'
import {
  SpecEditorDialog,
  type SpecEditorMode,
} from '../components/SpecEditorDialog'
import {
  bodySx,
  captionSx,
  pageCardFlushSx,
  sectionTitleSx,
  workspaceAccent,
  workspaceFontFamily,
  workspaceText,
} from '../../../shared'

function formatRelative(value: string | null | undefined): string {
  if (!value) return '—'
  try {
    return formatDistanceToNow(parseISO(value), { addSuffix: true })
  } catch {
    return '—'
  }
}

/**
 * Truncates a user id to a short, monospaced form. We don't yet have a
 * workspace member lookup hook on the frontend, so the audit field is just the
 * id today — keeping it short stops it from eating the whole column.
 */
function shortUserId(userId: string | null | undefined): string {
  if (!userId) return '—'
  if (userId.length <= 10) return userId
  return `${userId.slice(0, 8)}…`
}

/**
 * Workspace Spec Catalog — the "Specs" section of the workspace settings page.
 *
 * <p>Lists every catalog entry for the current workspace in a calm hairline
 * table, with a primary "Create spec" pill in the section header and a kebab
 * menu on each row for Edit / Duplicate / Delete. The list endpoint omits the
 * spec content; the editor dialog fetches it via the per-id endpoint when the
 * user clicks Edit, so the table query stays small even when a workspace
 * accumulates dozens of catalog specs.</p>
 *
 * <p>All mutations go through the generated Orval hooks inside the dialog
 * components, which invalidate the list query on success — so this tab
 * doesn't need to track the result itself, the table just re-renders.</p>
 */
export function SpecsTab() {
  const { currentWorkspace } = useWorkspace()
  const workspaceId = currentWorkspace?.id ?? ''

  const specsQuery = useGetApiWorkspacesWorkspaceIdSpecs(workspaceId, {
    query: { enabled: !!workspaceId },
  })

  const specs: WorkspaceSpecListItem[] = useMemo(
    () => specsQuery.data ?? [],
    [specsQuery.data],
  )

  // The kebab-menu anchor + the row it was opened from. Keeping these together
  // means the menu can't "stick" to a row that's been removed from the list
  // mid-render — both fields reset on close.
  const [menuAnchor, setMenuAnchor] = useState<HTMLElement | null>(null)
  const [menuSpec, setMenuSpec] = useState<WorkspaceSpecListItem | null>(null)

  const [editorOpen, setEditorOpen] = useState(false)
  const [editorMode, setEditorMode] = useState<SpecEditorMode>({
    kind: 'create',
  })

  const [duplicateOpen, setDuplicateOpen] = useState(false)
  const [duplicateSource, setDuplicateSource] =
    useState<WorkspaceSpecListItem | null>(null)

  const [deleteOpen, setDeleteOpen] = useState(false)
  const [deleteSpec, setDeleteSpec] = useState<WorkspaceSpecListItem | null>(
    null,
  )

  const openMenu = (
    event: React.MouseEvent<HTMLElement>,
    spec: WorkspaceSpecListItem,
  ) => {
    setMenuAnchor(event.currentTarget)
    setMenuSpec(spec)
  }
  const closeMenu = () => {
    setMenuAnchor(null)
    setMenuSpec(null)
  }

  const handleCreate = () => {
    setEditorMode({ kind: 'create' })
    setEditorOpen(true)
  }

  const handleEdit = (spec: WorkspaceSpecListItem) => {
    setEditorMode({ kind: 'edit', spec })
    setEditorOpen(true)
    closeMenu()
  }

  const handleDuplicate = (spec: WorkspaceSpecListItem) => {
    setDuplicateSource(spec)
    setDuplicateOpen(true)
    closeMenu()
  }

  const handleDelete = (spec: WorkspaceSpecListItem) => {
    setDeleteSpec(spec)
    setDeleteOpen(true)
    closeMenu()
  }

  if (!workspaceId) {
    return (
      <Alert
        severity="info"
        variant="quiet"
      >
        Loading workspace…
      </Alert>
    )
  }

  const isLoading = specsQuery.isLoading
  const isError = specsQuery.isError

  return (
    <Stack spacing={4}>
      <Box>
        <Typography
          component="h3"
          sx={{
            fontSize: '1.25rem',
            fontWeight: 400,
            letterSpacing: '-0.01em',
            color: workspaceText.primary,
            mb: 0.5,
          }}
        >
          Spec Catalog
        </Typography>
        <Typography sx={bodySx}>
          Reusable runtime specs for this workspace. Branches pick a spec at
          fork time — later edits never touch existing branches.
        </Typography>
      </Box>

      {isError && (
        <Alert
          severity="error"
          variant="quiet"
        >
          Could not load specs.
        </Alert>
      )}

      <Box sx={pageCardFlushSx}>
        <Stack
          direction="row"
          alignItems="center"
          justifyContent="space-between"
          sx={{
            px: { xs: 2.5, md: 3 },
            py: 2,
            borderBottom: 1,
            borderColor: 'instrument.hairline',
          }}
        >
          <Box>
            <Typography sx={sectionTitleSx}>
              {isLoading
                ? 'Loading…'
                : `${specs.length} ${specs.length === 1 ? 'spec' : 'specs'}`}
            </Typography>
          </Box>
          <Button
            variant="pill"
            color="primary"
            startIcon={<AddIcon fontSize="small" />}
            onClick={handleCreate}
            disabled={isLoading}
            sx={{ flexShrink: 0 }}
          >
            Create spec
          </Button>
        </Stack>

        {isLoading && !isError && (
          <Stack spacing={1.5} sx={{ p: { xs: 2.5, md: 3 } }}>
            <Skeleton variant="rounded" height={48} />
            <Skeleton variant="rounded" height={48} />
            <Skeleton variant="rounded" height={48} />
          </Stack>
        )}

        {!isLoading && !isError && specs.length === 0 && (
          <Box
            sx={{
              py: 6,
              px: 3,
              textAlign: 'center',
            }}
          >
            <Typography sx={{ ...sectionTitleSx, mb: 1 }}>
              No specs in this workspace's catalog yet.
            </Typography>
            <Typography sx={captionSx}>
              Click <strong>Create spec</strong> to add one, or save the spec
              from a running runtime via its spec drawer.
            </Typography>
          </Box>
        )}

        {!isLoading && !isError && specs.length > 0 && (
          <TableContainer>
            <Table size="small" sx={{ minWidth: 600 }}>
            <TableBody
              sx={{
                '& td': {
                  borderBottomColor: 'instrument.hairline',
                  fontFamily: workspaceFontFamily.sans,
                  fontSize: '0.875rem',
                  color: workspaceText.primary,
                  py: 1.5,
                },
                '& tr:last-child td': { borderBottom: 'none' },
              }}
            >
              {specs.map((spec) => (
                <TableRow
                  key={spec.id}
                  hover
                  sx={{
                    cursor: 'pointer',
                    transition: 'background-color 150ms ease',
                    '&:hover': {
                      backgroundColor: 'instrument.chipBg',
                    },
                  }}
                  onClick={() => handleEdit(spec)}
                >
                  <TableCell>
                    <Typography
                      sx={{
                        fontFamily: workspaceFontFamily.mono,
                        fontSize: '0.8125rem',
                        color: workspaceText.primary,
                      }}
                    >
                      {spec.name}
                    </Typography>
                  </TableCell>
                  <TableCell>
                    <Typography
                      sx={{
                        ...captionSx,
                        color: workspaceText.muted,
                        maxWidth: 320,
                        overflow: 'hidden',
                        textOverflow: 'ellipsis',
                        whiteSpace: 'nowrap',
                      }}
                    >
                      {spec.description?.trim() || '—'}
                    </Typography>
                  </TableCell>
                  <TableCell>
                    <Tooltip title={spec.updatedAt}>
                      <Typography
                        sx={{ ...captionSx, fontSize: '0.8125rem' }}
                      >
                        {formatRelative(spec.updatedAt)}
                      </Typography>
                    </Tooltip>
                  </TableCell>
                  <TableCell>
                    <Tooltip title={spec.updatedByUserId}>
                      <Typography
                        sx={{
                          fontFamily: workspaceFontFamily.mono,
                          fontSize: '0.75rem',
                          color: workspaceText.faint,
                        }}
                      >
                        {shortUserId(spec.updatedByUserId)}
                      </Typography>
                    </Tooltip>
                  </TableCell>
                  <TableCell align="right" sx={{ width: 56 }}>
                    <IconButton
                      size="small"
                      aria-label={`Actions for ${spec.name}`}
                      onClick={(e) => {
                        e.stopPropagation()
                        openMenu(e, spec)
                      }}
                      sx={{
                        color: workspaceText.muted,
                        '&:hover': {
                          color: workspaceAccent.ink,
                          backgroundColor: 'transparent',
                        },
                      }}
                    >
                      <MoreHorizIcon fontSize="small" />
                    </IconButton>
                  </TableCell>
                </TableRow>
              ))}
            </TableBody>
            </Table>
          </TableContainer>
        )}
      </Box>

      <Menu
        anchorEl={menuAnchor}
        open={!!menuAnchor && !!menuSpec}
        onClose={closeMenu}
        slotProps={{
          paper: {
            sx: {
              backgroundColor: 'instrument.canvas',
              border: 1,
              borderColor: 'instrument.hairline',
              boxShadow: 'none',
              minWidth: 160,
            },
          },
        }}
      >
        <MenuItem
          onClick={() => menuSpec && handleEdit(menuSpec)}
          sx={{
            fontFamily: workspaceFontFamily.sans,
            fontSize: '0.875rem',
            color: workspaceText.primary,
          }}
        >
          <EditOutlinedIcon
            fontSize="small"
            sx={{ mr: 1, color: workspaceText.muted }}
          />
          Edit
        </MenuItem>
        <MenuItem
          onClick={() => menuSpec && handleDuplicate(menuSpec)}
          sx={{
            fontFamily: workspaceFontFamily.sans,
            fontSize: '0.875rem',
            color: workspaceText.primary,
          }}
        >
          <ContentCopyOutlinedIcon
            fontSize="small"
            sx={{ mr: 1, color: workspaceText.muted }}
          />
          Duplicate
        </MenuItem>
        <MenuItem
          onClick={() => menuSpec && handleDelete(menuSpec)}
          sx={{
            fontFamily: workspaceFontFamily.sans,
            fontSize: '0.875rem',
            color: workspaceAccent.ink,
          }}
        >
          <DeleteOutlineIcon
            fontSize="small"
            sx={{ mr: 1, color: workspaceAccent.ink }}
          />
          Delete
        </MenuItem>
      </Menu>

      <SpecEditorDialog
        open={editorOpen}
        onClose={() => setEditorOpen(false)}
        workspaceId={workspaceId}
        mode={editorMode}
      />
      <DuplicateSpecDialog
        open={duplicateOpen}
        onClose={() => setDuplicateOpen(false)}
        workspaceId={workspaceId}
        source={duplicateSource}
      />
      <DeleteSpecDialog
        open={deleteOpen}
        onClose={() => setDeleteOpen(false)}
        workspaceId={workspaceId}
        spec={deleteSpec}
      />
    </Stack>
  )
}

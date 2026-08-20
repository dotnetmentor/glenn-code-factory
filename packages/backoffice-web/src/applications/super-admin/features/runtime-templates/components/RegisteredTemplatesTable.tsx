import { useMemo, useState } from 'react'
import {
  Box,
  Button,
  Chip,
  CircularProgress,
  Dialog,
  DialogActions,
  DialogContent,
  DialogContentText,
  DialogTitle,
  Stack,
  Tooltip,
  Typography,
  alpha,
} from '@mui/material'
import { DataGrid } from '@mui/x-data-grid'
import type { GridColDef, GridRowClassNameParams } from '@mui/x-data-grid'
import { formatDistanceToNow } from 'date-fns'
import {
  RuntimeTemplate,
  RuntimeTemplateStatus,
} from '../../../../../api/queries-commands'

interface RegisteredTemplatesTableProps {
  rows: RuntimeTemplate[]
  loading: boolean
  pendingActionId: string | null
  onActivate: (template: RuntimeTemplate) => void
  onDeprecate: (template: RuntimeTemplate) => void
  onYank: (template: RuntimeTemplate) => void
}

type StatusChipColor = 'success' | 'default' | 'error'

const STATUS_CHIP: Record<RuntimeTemplateStatus, { label: string; color: StatusChipColor }> = {
  Active: { label: 'Active', color: 'success' },
  Deprecated: { label: 'Deprecated', color: 'default' },
  Yanked: { label: 'Yanked', color: 'error' },
}

function safeRelativeTime(iso: string | null | undefined): string {
  if (!iso) return '—'
  const date = new Date(iso)
  if (Number.isNaN(date.getTime())) return '—'
  return `${formatDistanceToNow(date)} ago`
}

function shortSha(sha: string | null | undefined): string {
  if (!sha) return '—'
  return sha.slice(0, 7)
}

function truncate(value: string | null | undefined, max = 60): string {
  if (!value) return ''
  return value.length <= max ? value : `${value.slice(0, max)}…`
}

export function RegisteredTemplatesTable({
  rows,
  loading,
  pendingActionId,
  onActivate,
  onDeprecate,
  onYank,
}: RegisteredTemplatesTableProps) {
  const [yankCandidate, setYankCandidate] = useState<RuntimeTemplate | null>(null)

  const columns = useMemo<GridColDef<RuntimeTemplate>[]>(
    () => [
      {
        field: 'boxId',
        headerName: 'Box ID',
        width: 160,
        renderCell: (p) => (
          <Tooltip title={(p.value as string) || ''} placement="top" arrow>
            <Typography
              variant="body2"
              sx={{
                fontFamily: '"SF Mono", "Fira Code", "Consolas", monospace',
                fontSize: '0.8rem',
                fontWeight: 500,
              }}
            >
              {p.value as string}
            </Typography>
          </Tooltip>
        ),
      },
      {
        field: 'label',
        headerName: 'Label',
        flex: 1.2,
        minWidth: 180,
        renderCell: (p) => (
          <Typography variant="body2" sx={{ fontSize: '0.8rem', fontWeight: 500 }}>
            {p.value as string}
          </Typography>
        ),
      },
      {
        field: 'gitSha',
        headerName: 'Git SHA',
        width: 110,
        renderCell: (p) => (
          <Tooltip title={(p.value as string) || ''} placement="top" arrow>
            <Typography
              variant="body2"
              sx={{
                fontFamily: '"SF Mono", "Fira Code", "Consolas", monospace',
                fontSize: '0.8rem',
              }}
            >
              {shortSha(p.value as string | null | undefined)}
            </Typography>
          </Tooltip>
        ),
      },
      {
        field: 'builtAt',
        headerName: 'Built',
        width: 150,
        renderCell: (p) => (
          <Tooltip title={(p.value as string) ?? ''} placement="top" arrow>
            <Typography variant="body2" sx={{ fontSize: '0.8rem', color: 'text.secondary' }}>
              {safeRelativeTime(p.value as string | null | undefined)}
            </Typography>
          </Tooltip>
        ),
      },
      {
        field: 'status',
        headerName: 'Status',
        width: 130,
        sortable: true,
        renderCell: (p) => {
          const status = p.value as RuntimeTemplateStatus
          const meta = STATUS_CHIP[status] ?? { label: status, color: 'default' as StatusChipColor }
          return (
            <Chip
              label={meta.label}
              size="small"
              color={meta.color}
              variant={meta.color === 'default' ? 'outlined' : 'filled'}
              sx={{ fontWeight: 600, fontSize: '0.7rem' }}
            />
          )
        },
      },
      {
        field: 'notes',
        headerName: 'Notes',
        flex: 1,
        minWidth: 160,
        sortable: false,
        renderCell: (p) => {
          const value = (p.value as string | null | undefined) ?? ''
          if (!value) {
            return (
              <Typography variant="body2" sx={{ color: 'text.disabled', fontSize: '0.8rem' }}>
                {'—'}
              </Typography>
            )
          }
          return (
            <Tooltip title={value} placement="top" arrow>
              <Typography variant="body2" sx={{ fontSize: '0.8rem', color: 'text.secondary' }}>
                {truncate(value)}
              </Typography>
            </Tooltip>
          )
        },
      },
      {
        field: 'actions',
        headerName: 'Actions',
        width: 280,
        sortable: false,
        filterable: false,
        renderCell: (p) => {
          const template = p.row
          const isPending = pendingActionId === template.id
          const isActive = template.status === 'Active'
          const isYanked = template.status === 'Yanked'
          const isDeprecated = template.status === 'Deprecated'

          return (
            <Stack direction="row" spacing={0.75} sx={{ alignItems: 'center' }}>
              <Button
                size="small"
                variant="contained"
                color="success"
                disabled={isActive || isPending}
                onClick={() => onActivate(template)}
                sx={{
                  textTransform: 'none',
                  fontSize: '0.75rem',
                  boxShadow: 'none',
                  '&:hover': { boxShadow: 'none' },
                }}
              >
                Activate
              </Button>
              <Button
                size="small"
                variant="outlined"
                disabled={isDeprecated || isPending}
                onClick={() => onDeprecate(template)}
                sx={{ textTransform: 'none', fontSize: '0.75rem' }}
              >
                Deprecate
              </Button>
              <Button
                size="small"
                variant="outlined"
                color="error"
                disabled={isYanked || isPending}
                onClick={() => setYankCandidate(template)}
                sx={{ textTransform: 'none', fontSize: '0.75rem' }}
              >
                Yank
              </Button>
              {isPending ? <CircularProgress size={16} sx={{ ml: 0.5 }} /> : null}
            </Stack>
          )
        },
      },
    ],
    [onActivate, onDeprecate, pendingActionId],
  )

  const getRowClassName = (params: GridRowClassNameParams<RuntimeTemplate>) =>
    params.row.status === 'Active' ? 'runtime-templates-row--active' : ''

  return (
    <>
      <Box
        sx={{
          width: '100%',
          '& .runtime-templates-row--active': {
            bgcolor: (theme) => alpha(theme.palette.success.main, 0.08),
            '&:hover': {
              bgcolor: (theme) => alpha(theme.palette.success.main, 0.12),
            },
          },
        }}
      >
        <DataGrid
          autoHeight
          rows={rows}
          columns={columns}
          loading={loading}
          getRowClassName={getRowClassName}
          disableRowSelectionOnClick
          disableColumnMenu
          pageSizeOptions={[10, 25, 50]}
          initialState={{
            pagination: { paginationModel: { pageSize: 25, page: 0 } },
            sorting: { sortModel: [{ field: 'builtAt', sort: 'desc' }] },
          }}
          slots={{
            noRowsOverlay: () => (
              <Stack alignItems="center" justifyContent="center" sx={{ height: '100%', p: 4 }}>
                <Typography variant="body2" color="text.secondary">
                  No templates registered yet. Pick a box from the candidates below.
                </Typography>
              </Stack>
            ),
          }}
          sx={{
            border: 'none',
            '& .MuiDataGrid-cell:focus, & .MuiDataGrid-cell:focus-within': { outline: 'none' },
            '& .MuiDataGrid-columnHeader:focus, & .MuiDataGrid-columnHeader:focus-within': {
              outline: 'none',
            },
          }}
        />
      </Box>

      <Dialog
        open={!!yankCandidate}
        onClose={() => setYankCandidate(null)}
        maxWidth="xs"
        fullWidth
      >
        <DialogTitle>Yank template?</DialogTitle>
        <DialogContent>
          <DialogContentText>
            This template will not be selectable for new runtimes.
          </DialogContentText>
          {yankCandidate ? (
            <Typography
              variant="body2"
              sx={{
                mt: 2,
                fontFamily: '"SF Mono", "Fira Code", "Consolas", monospace',
                fontSize: '0.8rem',
                color: 'text.secondary',
              }}
            >
              {yankCandidate.label} ({yankCandidate.boxId})
            </Typography>
          ) : null}
        </DialogContent>
        <DialogActions>
          <Button onClick={() => setYankCandidate(null)} sx={{ textTransform: 'none' }}>
            Cancel
          </Button>
          <Button
            color="error"
            variant="contained"
            onClick={() => {
              if (yankCandidate) {
                onYank(yankCandidate)
              }
              setYankCandidate(null)
            }}
            sx={{ textTransform: 'none', boxShadow: 'none', '&:hover': { boxShadow: 'none' } }}
          >
            Yank
          </Button>
        </DialogActions>
      </Dialog>
    </>
  )
}

import { useMemo } from 'react'
import {
  Box,
  Chip,
  IconButton,
  Stack,
  Tooltip,
  Typography,
} from '@mui/material'
import EditIcon from '@mui/icons-material/Edit'
import ArchiveOutlinedIcon from '@mui/icons-material/ArchiveOutlined'
import StarRoundedIcon from '@mui/icons-material/StarRounded'
import { DataGrid } from '@mui/x-data-grid'
import type { GridColDef } from '@mui/x-data-grid'
import type { ProjectTemplateListItem } from '../../../../../api/queries-commands'

interface StartersTableProps {
  rows: ProjectTemplateListItem[]
  loading: boolean
  onEdit: (row: ProjectTemplateListItem) => void
  onArchive: (row: ProjectTemplateListItem) => void
}

function truncate(value: string | null | undefined, max = 60): string {
  if (!value) return ''
  return value.length <= max ? value : `${value.slice(0, max)}\u2026`
}

const DEFAULT_GOLD = 'rgb(200, 147, 42)'

/**
 * DataGrid for the Starters (ProjectTemplates) catalogue. Mirrors the rhythm
 * of {@code AgentModelsTable} so the super-admin surface reads as one product —
 * same column rhythm, same row-action style, same calm chrome.
 *
 * Archived rows are rendered with reduced opacity and an "Archived" chip so
 * super-admins can still see history without the catalog feeling cluttered.
 */
export function StartersTable({
  rows,
  loading,
  onEdit,
  onArchive,
}: StartersTableProps) {
  const columns = useMemo<GridColDef<ProjectTemplateListItem>[]>(
    () => [
      {
        field: 'name',
        headerName: 'Name',
        flex: 1.2,
        minWidth: 180,
        renderCell: (p) => {
          const row = p.row
          return (
            <Stack
              direction="row"
              spacing={0.5}
              sx={{ alignItems: 'center', minWidth: 0 }}
            >
              <Typography
                variant="body2"
                sx={{ fontSize: '0.8125rem', fontWeight: 500 }}
                noWrap
              >
                {row.name}
              </Typography>
              {row.isDefault ? (
                <Tooltip title="Default Starter — pre-selected in the new-project picker">
                  <StarRoundedIcon
                    sx={{
                      fontSize: '0.95rem',
                      color: DEFAULT_GOLD,
                      flexShrink: 0,
                    }}
                  />
                </Tooltip>
              ) : null}
            </Stack>
          )
        },
      },
      {
        field: 'slug',
        headerName: 'Slug',
        flex: 0.9,
        minWidth: 160,
        renderCell: (p) => (
          <Typography
            variant="body2"
            sx={{
              fontFamily: '"SF Mono", "Fira Code", "Consolas", monospace',
              fontSize: '0.8rem',
              color: 'text.secondary',
            }}
          >
            {p.value as string}
          </Typography>
        ),
      },
      {
        field: 'description',
        headerName: 'Description',
        flex: 1.3,
        minWidth: 200,
        sortable: false,
        renderCell: (p) => {
          const value = (p.value as string | null | undefined) ?? ''
          if (!value) {
            return (
              <Typography
                variant="body2"
                sx={{ color: 'text.disabled', fontSize: '0.8rem' }}
              >
                {'\u2014'}
              </Typography>
            )
          }
          return (
            <Tooltip title={value} placement="top" arrow>
              <Typography
                variant="body2"
                sx={{ fontSize: '0.8rem', color: 'text.secondary' }}
              >
                {truncate(value)}
              </Typography>
            </Tooltip>
          )
        },
      },
      {
        field: 'repo',
        headerName: 'Repo',
        flex: 1,
        minWidth: 200,
        sortable: false,
        renderCell: (p) => {
          const row = p.row
          const repo = `${row.sourceRepoOwner}/${row.sourceRepoName}`
          return (
            <Typography
              variant="body2"
              sx={{
                fontFamily: '"SF Mono", "Fira Code", "Consolas", monospace',
                fontSize: '0.78rem',
                color: 'text.secondary',
              }}
              noWrap
            >
              {repo}
            </Typography>
          )
        },
      },
      {
        field: 'hasRuntimeSpec',
        headerName: 'Runtime Spec',
        width: 130,
        renderCell: (p) => (
          <Chip
            label={(p.value as boolean) ? 'Yes' : 'No'}
            size="small"
            color={(p.value as boolean) ? 'info' : 'default'}
            variant={(p.value as boolean) ? 'filled' : 'outlined'}
            sx={{ fontWeight: 600, fontSize: '0.7rem' }}
          />
        ),
      },
      {
        field: 'isActive',
        headerName: 'Active',
        width: 110,
        renderCell: (p) => {
          const row = p.row
          if (row.isArchived) {
            return (
              <Chip
                label="Archived"
                size="small"
                color="default"
                variant="outlined"
                sx={{ fontWeight: 600, fontSize: '0.7rem' }}
              />
            )
          }
          return (
            <Chip
              label={(p.value as boolean) ? 'Active' : 'Hidden'}
              size="small"
              color={(p.value as boolean) ? 'success' : 'default'}
              variant={(p.value as boolean) ? 'filled' : 'outlined'}
              sx={{ fontWeight: 600, fontSize: '0.7rem' }}
            />
          )
        },
      },
      {
        field: 'sortOrder',
        headerName: 'Sort',
        width: 80,
        type: 'number',
        renderCell: (p) => (
          <Typography
            variant="body2"
            sx={{ fontSize: '0.8rem', color: 'text.secondary' }}
          >
            {p.value as number}
          </Typography>
        ),
      },
      {
        field: 'updatedAt',
        headerName: 'Last edited',
        width: 150,
        renderCell: (p) => {
          const raw = p.value as string | undefined
          if (!raw) {
            return (
              <Typography
                variant="body2"
                sx={{ color: 'text.disabled', fontSize: '0.8rem' }}
              >
                {'\u2014'}
              </Typography>
            )
          }
          const d = new Date(raw)
          const label = Number.isNaN(d.getTime())
            ? raw
            : d.toLocaleDateString(undefined, {
                year: 'numeric',
                month: 'short',
                day: '2-digit',
              })
          return (
            <Tooltip title={raw} arrow>
              <Typography
                variant="body2"
                sx={{ fontSize: '0.8rem', color: 'text.secondary' }}
              >
                {label}
              </Typography>
            </Tooltip>
          )
        },
      },
      {
        field: 'actions',
        headerName: 'Actions',
        width: 110,
        sortable: false,
        filterable: false,
        renderCell: (p) => {
          const row = p.row
          return (
            <Stack direction="row" spacing={0.5} sx={{ alignItems: 'center' }}>
              <Tooltip title="Edit">
                <IconButton
                  size="small"
                  onClick={() => onEdit(row)}
                  aria-label="Edit Starter"
                >
                  <EditIcon sx={{ fontSize: 18 }} />
                </IconButton>
              </Tooltip>
              <Tooltip title={row.isArchived ? 'Already archived' : 'Archive'}>
                <span>
                  <IconButton
                    size="small"
                    color="error"
                    onClick={() => onArchive(row)}
                    disabled={row.isArchived}
                    aria-label="Archive Starter"
                  >
                    <ArchiveOutlinedIcon sx={{ fontSize: 18 }} />
                  </IconButton>
                </span>
              </Tooltip>
            </Stack>
          )
        },
      },
    ],
    [onEdit, onArchive],
  )

  return (
    <Box sx={{ width: '100%' }}>
      <DataGrid
        autoHeight
        rows={rows}
        columns={columns}
        loading={loading}
        getRowId={(row) => row.id}
        disableRowSelectionOnClick
        disableColumnMenu
        pageSizeOptions={[10, 25, 50]}
        initialState={{
          pagination: { paginationModel: { pageSize: 25, page: 0 } },
          sorting: { sortModel: [{ field: 'sortOrder', sort: 'asc' }] },
        }}
        getRowClassName={(params) =>
          params.row.isArchived ? 'starter-row--archived' : ''
        }
        slots={{
          noRowsOverlay: () => (
            <Stack
              alignItems="center"
              justifyContent="center"
              sx={{ height: '100%', p: 4 }}
            >
              <Typography variant="body2" color="text.secondary">
                No Starters yet. Create one to surface it in the new-project
                picker.
              </Typography>
            </Stack>
          ),
        }}
        sx={{
          border: 'none',
          '& .MuiDataGrid-cell:focus, & .MuiDataGrid-cell:focus-within': {
            outline: 'none',
          },
          '& .MuiDataGrid-columnHeader:focus, & .MuiDataGrid-columnHeader:focus-within':
            {
              outline: 'none',
            },
          '& .starter-row--archived': {
            opacity: 0.55,
          },
        }}
      />
    </Box>
  )
}

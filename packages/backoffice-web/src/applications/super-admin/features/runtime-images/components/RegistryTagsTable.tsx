import { useMemo } from 'react'
import {
  Box,
  Button,
  Chip,
  Stack,
  Tooltip,
  Typography,
} from '@mui/material'
import { DataGrid } from '@mui/x-data-grid'
import type { GridColDef } from '@mui/x-data-grid'
import { formatDistanceToNow } from 'date-fns'
import { RegistryTagDto } from '../../../../../api/queries-commands'
import { RUNTIME_IMAGE_REGISTRY } from '../runtimeRegistry'

interface RegistryTagsTableProps {
  rows: RegistryTagDto[]
  loading: boolean
  registeredTagSet: Set<string>
  onRegister: (tag: RegistryTagDto) => void
}

function shortDigest(digest: string | null | undefined): string {
  if (!digest) return '\u2014'
  const stripped = digest.startsWith('sha256:') ? digest.slice(7) : digest
  return stripped.slice(0, 12)
}

function bytesToMb(bytes: number | null | undefined): string {
  if (bytes == null) return '\u2014'
  const mb = Math.round(bytes / 1024 / 1024)
  return `${mb} MB`
}

function safeRelativeTime(iso: string | null | undefined): string {
  if (!iso) return '\u2014'
  const date = new Date(iso)
  if (Number.isNaN(date.getTime())) return '\u2014'
  return `${formatDistanceToNow(date)} ago`
}

function shortSha(sha: string | null | undefined): string {
  if (!sha) return '\u2014'
  return sha.slice(0, 7)
}

export function RegistryTagsTable({
  rows,
  loading,
  registeredTagSet,
  onRegister,
}: RegistryTagsTableProps) {
  const columns = useMemo<GridColDef<RegistryTagDto>[]>(
    () => [
      {
        field: 'tag',
        headerName: 'Tag',
        flex: 1.4,
        minWidth: 220,
        renderCell: (p) => (
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
        ),
      },
      {
        field: 'digest',
        headerName: 'Digest',
        width: 150,
        sortable: false,
        renderCell: (p) => (
          <Tooltip title={(p.value as string) || ''} placement="top" arrow>
            <Typography
              variant="body2"
              sx={{
                fontFamily: '"SF Mono", "Fira Code", "Consolas", monospace',
                fontSize: '0.8rem',
              }}
            >
              {shortDigest(p.value as string | null | undefined)}
            </Typography>
          </Tooltip>
        ),
      },
      {
        field: 'sizeBytes',
        headerName: 'Size',
        width: 100,
        valueFormatter: (p: { value?: number | null }) => bytesToMb(p?.value),
      },
      {
        field: 'pushedAt',
        headerName: 'Pushed',
        width: 150,
        renderCell: (p) => {
          const value = p.value as string | null | undefined
          return (
            <Tooltip title={value ?? ''} placement="top" arrow>
              <Typography variant="body2" sx={{ fontSize: '0.8rem', color: 'text.secondary' }}>
                {safeRelativeTime(value)}
              </Typography>
            </Tooltip>
          )
        },
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
        field: 'status',
        headerName: 'Status',
        width: 160,
        sortable: false,
        renderCell: (p) => {
          const isRegistered = registeredTagSet.has(p.row.tag)
          if (!isRegistered) return null
          return (
            <Chip
              label="Already registered"
              size="small"
              color="default"
              variant="outlined"
              sx={{ fontWeight: 500, fontSize: '0.7rem' }}
            />
          )
        },
      },
      {
        field: 'actions',
        headerName: 'Action',
        width: 120,
        sortable: false,
        filterable: false,
        renderCell: (p) => {
          const isRegistered = registeredTagSet.has(p.row.tag)
          return (
            <Button
              size="small"
              variant="contained"
              disabled={isRegistered}
              onClick={() => onRegister(p.row)}
              sx={{
                textTransform: 'none',
                fontSize: '0.75rem',
                boxShadow: 'none',
                '&:hover': { boxShadow: 'none' },
              }}
            >
              Register
            </Button>
          )
        },
      },
    ],
    [registeredTagSet, onRegister],
  )

  return (
    <Box sx={{ width: '100%' }}>
      <DataGrid
        autoHeight
        rows={rows}
        getRowId={(row) => row.tag}
        columns={columns}
        loading={loading}
        disableRowSelectionOnClick
        disableColumnMenu
        pageSizeOptions={[10, 25, 50]}
        initialState={{
          pagination: { paginationModel: { pageSize: 25, page: 0 } },
          sorting: { sortModel: [{ field: 'pushedAt', sort: 'desc' }] },
        }}
        slots={{
          noRowsOverlay: () => (
            <Stack alignItems="center" justifyContent="center" sx={{ height: '100%', p: 4 }}>
              <Typography variant="body2" color="text.secondary">
                No images pushed to{' '}
                <Box
                  component="span"
                  sx={{
                    fontFamily: '"SF Mono", "Fira Code", "Consolas", monospace',
                    fontSize: '0.8rem',
                  }}
                >
                  {RUNTIME_IMAGE_REGISTRY}
                </Box>{' '}
                yet.
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
  )
}

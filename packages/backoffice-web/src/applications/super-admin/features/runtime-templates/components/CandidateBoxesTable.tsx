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
import { TemplateCandidateBoxDto } from '../../../../../api/queries-commands'

interface CandidateBoxesTableProps {
  rows: TemplateCandidateBoxDto[]
  loading: boolean
  onRegister: (box: TemplateCandidateBoxDto) => void
}

function safeRelativeTime(iso: string | null | undefined): string {
  if (!iso) return '—'
  const date = new Date(iso)
  if (Number.isNaN(date.getTime())) return '—'
  return `${formatDistanceToNow(date)} ago`
}

/**
 * Discovery table for the runtime-templates page — every live box on the Box
 * account is a potential golden template. Rows already registered as a
 * template are disabled (registering the same box twice makes no sense);
 * everything else gets a Register action.
 */
export function CandidateBoxesTable({
  rows,
  loading,
  onRegister,
}: CandidateBoxesTableProps) {
  const columns = useMemo<GridColDef<TemplateCandidateBoxDto>[]>(
    () => [
      {
        field: 'id',
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
        field: 'name',
        headerName: 'Name',
        flex: 1.2,
        minWidth: 180,
        renderCell: (p) => {
          const value = p.value as string | null | undefined
          if (!value) {
            return (
              <Typography variant="body2" sx={{ color: 'text.disabled', fontSize: '0.8rem' }}>
                {'—'}
              </Typography>
            )
          }
          return (
            <Typography
              variant="body2"
              sx={{
                fontFamily: '"SF Mono", "Fira Code", "Consolas", monospace',
                fontSize: '0.8rem',
              }}
            >
              {value}
            </Typography>
          )
        },
      },
      {
        field: 'status',
        headerName: 'Status',
        width: 120,
        renderCell: (p) => (
          <Chip
            label={p.value as string}
            size="small"
            variant="outlined"
            sx={{ fontWeight: 500, fontSize: '0.7rem' }}
          />
        ),
      },
      {
        field: 'size',
        headerName: 'Size',
        width: 100,
        renderCell: (p) => (
          <Typography variant="body2" sx={{ fontSize: '0.8rem' }}>
            {(p.value as string | null | undefined) ?? '—'}
          </Typography>
        ),
      },
      {
        field: 'region',
        headerName: 'Region',
        width: 100,
        renderCell: (p) => (
          <Typography variant="body2" sx={{ fontSize: '0.8rem' }}>
            {(p.value as string | null | undefined) ?? '—'}
          </Typography>
        ),
      },
      {
        field: 'createdAt',
        headerName: 'Created',
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
        field: 'alreadyRegistered',
        headerName: 'Registered',
        width: 160,
        sortable: false,
        renderCell: (p) => {
          if (!p.row.alreadyRegistered) return null
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
        renderCell: (p) => (
          <Button
            size="small"
            variant="contained"
            disabled={p.row.alreadyRegistered}
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
        ),
      },
    ],
    [onRegister],
  )

  return (
    <Box sx={{ width: '100%' }}>
      <DataGrid
        autoHeight
        rows={rows}
        columns={columns}
        loading={loading}
        disableRowSelectionOnClick
        disableColumnMenu
        pageSizeOptions={[10, 25, 50]}
        initialState={{
          pagination: { paginationModel: { pageSize: 25, page: 0 } },
          sorting: { sortModel: [{ field: 'createdAt', sort: 'desc' }] },
        }}
        slots={{
          noRowsOverlay: () => (
            <Stack alignItems="center" justifyContent="center" sx={{ height: '100%', p: 4 }}>
              <Typography variant="body2" color="text.secondary">
                No live boxes on the Box account. Build one with{' '}
                <Box
                  component="span"
                  sx={{
                    fontFamily: '"SF Mono", "Fira Code", "Consolas", monospace',
                    fontSize: '0.8rem',
                  }}
                >
                  scripts/build-box-template.sh
                </Box>{' '}
                first.
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

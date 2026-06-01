import { DataGrid, GridActionsCellItem } from '@mui/x-data-grid'
import type { GridColDef, GridPaginationModel, GridSortModel } from '@mui/x-data-grid'
import { Box, Typography, TextField, InputAdornment, Button, CircularProgress } from '@mui/material'
import { useMemo, useState, useEffect, useRef } from 'react'
import EditIcon from '@mui/icons-material/Edit'
import SearchIcon from '@mui/icons-material/Search'
import DownloadIcon from '@mui/icons-material/Download'
import { useGetApiUsers, getApiUsers, UserListItem } from '../../../../../api/queries-commands'
import { formatSwedishDateTime, useDebounce } from '../../../../shared/utils'
import ExcelJS from 'exceljs'

interface UsersTableProps {
  onEditUser?: (user: UserListItem) => void
}

export function UsersTable({ onEditUser }: UsersTableProps) {
  const [paginationModel, setPaginationModel] = useState<GridPaginationModel>({ page: 0, pageSize: 20 })
  const [sortModel, setSortModel] = useState<GridSortModel>([])
  const [searchValue, setSearchValue] = useState('')
  const [isExporting, setIsExporting] = useState(false)
  const debouncedSearch = useDebounce(searchValue, 500)
  const previousSearchRef = useRef(debouncedSearch)

  useEffect(() => {
    if (debouncedSearch !== previousSearchRef.current) {
      setPaginationModel(prev => ({ ...prev, page: 0 }))
      previousSearchRef.current = debouncedSearch
    }
  }, [debouncedSearch])

  const params = useMemo(() => ({
    search: debouncedSearch || undefined,
    page: paginationModel.page + 1,
    pageSize: paginationModel.pageSize,
  }), [debouncedSearch, paginationModel])

  const query = useGetApiUsers(params, { query: { staleTime: 30000 } })

  const rows = query.data?.users ?? []
  const rowCount = query.data?.totalCount ?? 0

  const handleExportToExcel = async () => {
    try {
      setIsExporting(true)
      const data = await getApiUsers({ search: debouncedSearch || undefined, fetchAll: true })
      const allUsers = data.users || []

      const workbook = new ExcelJS.Workbook()
      const worksheet = workbook.addWorksheet('Users')

      worksheet.columns = [
        { header: 'Email', key: 'email', width: 30 },
        { header: 'Full Name', key: 'fullName', width: 25 },
        { header: 'Roles', key: 'roles', width: 30 },
        { header: 'Created At', key: 'createdAt', width: 20 },
      ]

      worksheet.getRow(1).font = { bold: true }
      worksheet.getRow(1).fill = { type: 'pattern', pattern: 'solid', fgColor: { argb: 'FFE0E0E0' } }

      allUsers.forEach((user) => {
        worksheet.addRow({
          email: user.email,
          fullName: user.fullName,
          roles: user.roles?.join(', ') || '-',
          createdAt: formatSwedishDateTime(user.createdAt),
        })
      })

      const buffer = await workbook.xlsx.writeBuffer()
      const blob = new Blob([buffer], { type: 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet' })
      const timestamp = new Date().toISOString().replace(/[:.]/g, '-').slice(0, -5)
      const url = window.URL.createObjectURL(blob)
      const link = document.createElement('a')
      link.href = url
      link.download = `Users_${timestamp}.xlsx`
      document.body.appendChild(link)
      link.click()
      document.body.removeChild(link)
      window.URL.revokeObjectURL(url)
    } catch (error) {
      console.error('Export failed:', error)
      alert('Failed to export users')
    } finally {
      setIsExporting(false)
    }
  }

  const columns = useMemo<GridColDef<UserListItem>[]>(() => [
    {
      field: 'email',
      headerName: 'Email',
      flex: 1,
      renderCell: (p) => <a style={{ color: 'inherit' }}>{p.value as string}</a>,
    },
    { field: 'fullName', headerName: 'Name', flex: 1 },
    {
      field: 'roles',
      headerName: 'Roles',
      flex: 1,
      sortable: false,
      renderCell: (params) => (
        <Box sx={{ display: 'flex', gap: 0.5, flexWrap: 'wrap', py: 0.5 }}>
          {(params.value as string[] || []).map((role) => (
            <Typography variant="body2" key={role}>{role}</Typography>
          ))}
        </Box>
      ),
    },
    {
      field: 'createdAt',
      headerName: 'Created',
      flex: 1,
      valueFormatter: (p: { value?: string }) => formatSwedishDateTime(p?.value) || '',
    },
    {
      field: 'actions',
      headerName: 'Actions',
      type: 'actions',
      width: 100,
      getActions: (params) => [
        <GridActionsCellItem
          key="edit"
          icon={<EditIcon />}
          label="Edit"
          onClick={() => onEditUser?.(params.row)}
          showInMenu={false}
        />,
      ],
    },
  ], [onEditUser])

  return (
    <Box sx={{ width: '100%' }}>
      <Box sx={{ mb: 2, display: 'flex', gap: 2, alignItems: 'center' }}>
        <TextField
          fullWidth
          placeholder="Search by email or name..."
          value={searchValue}
          onChange={(e) => setSearchValue(e.target.value)}
          InputProps={{
            startAdornment: <InputAdornment position="start"><SearchIcon /></InputAdornment>,
          }}
        />
        <Button
          variant="text"
          startIcon={isExporting ? <CircularProgress size={20} color="inherit" /> : <DownloadIcon />}
          onClick={handleExportToExcel}
          disabled={isExporting || query.isLoading}
          sx={{ minWidth: 180, whiteSpace: 'nowrap' }}
        >
          {isExporting ? 'Exporting...' : 'Export to Excel'}
        </Button>
      </Box>

      <DataGrid
        autoHeight
        rows={rows}
        columns={columns}
        paginationMode="server"
        rowCount={rowCount}
        paginationModel={paginationModel}
        onPaginationModelChange={(model) => query.isLoading ? null : setPaginationModel(model)}
        pageSizeOptions={[10, 20, 50]}
        loading={query.isFetching}
        sortingMode="client"
        sortModel={sortModel}
        onSortModelChange={setSortModel}
      />
    </Box>
  )
}

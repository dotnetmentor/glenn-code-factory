import { Box, ButtonBase, TextField, InputAdornment, Typography, IconButton, alpha } from '@mui/material'
import { useMemo, useState, useEffect, useRef } from 'react'
import SearchIcon from '@mui/icons-material/Search'
import CompareArrowsIcon from '@mui/icons-material/CompareArrows'
import NavigateBeforeIcon from '@mui/icons-material/NavigateBefore'
import NavigateNextIcon from '@mui/icons-material/NavigateNext'
import { useGetApiEntityChanges, EntityChangeListItem } from '../../../../../api/queries-commands'
import { formatSwedishDateTime, useDebounce } from '../../../../shared/utils'

interface EntityChangesTableProps {
  onViewChange?: (change: EntityChangeListItem) => void
}

const operationColorMap: Record<string, { bg: string; text: string; dot: string }> = {
  Created: { bg: 'success.light', text: 'success.dark', dot: 'success.main' },
  Updated: { bg: 'info.light', text: 'info.dark', dot: 'info.main' },
  Deleted: { bg: 'error.light', text: 'error.dark', dot: 'error.main' },
}

function getOperationColor(operation: string) {
  const key = Object.keys(operationColorMap).find(k => operation.toLowerCase().includes(k.toLowerCase()))
  if (key) return operationColorMap[key]
  return { bg: 'grey.100', text: 'text.secondary', dot: 'text.disabled' }
}

function countChangedProperties(changedProperties: string): number {
  try {
    const parsed = JSON.parse(changedProperties)
    if (Array.isArray(parsed)) return parsed.length
    if (typeof parsed === 'object' && parsed !== null) return Object.keys(parsed).length
    return 0
  } catch {
    return 0
  }
}

function timeAgo(dateStr: string): string {
  const now = new Date()
  const date = new Date(dateStr)
  const diff = now.getTime() - date.getTime()
  const seconds = Math.floor(diff / 1000)
  const minutes = Math.floor(seconds / 60)
  const hours = Math.floor(minutes / 60)
  const days = Math.floor(hours / 24)

  if (seconds < 60) return 'now'
  if (minutes < 60) return `${minutes}m`
  if (hours < 24) return `${hours}h`
  if (days < 7) return `${days}d`
  return formatSwedishDateTime(dateStr) || ''
}

export function EntityChangesTable({ onViewChange }: EntityChangesTableProps) {
  const [page, setPage] = useState(0)
  const pageSize = 30
  const [searchValue, setSearchValue] = useState('')
  const debouncedSearch = useDebounce(searchValue, 500)
  const previousSearchRef = useRef(debouncedSearch)

  useEffect(() => {
    if (debouncedSearch !== previousSearchRef.current) {
      setPage(0)
      previousSearchRef.current = debouncedSearch
    }
  }, [debouncedSearch])

  const params = useMemo(() => ({
    search: debouncedSearch || undefined,
    page: page + 1,
    pageSize,
  }), [debouncedSearch, page])

  const query = useGetApiEntityChanges(params, { query: { staleTime: 30000 } })
  const rows = query.data?.changes ?? []
  const totalCount = query.data?.totalCount ?? 0
  const totalPages = Math.ceil(totalCount / pageSize)

  return (
    <Box>
      {/* Search */}
      <TextField
        fullWidth
        size="small"
        placeholder="Search entity changes..."
        value={searchValue}
        onChange={(e) => setSearchValue(e.target.value)}
        InputProps={{
          startAdornment: (
            <InputAdornment position="start">
              <SearchIcon sx={{ color: 'text.disabled', fontSize: 18 }} />
            </InputAdornment>
          ),
        }}
        sx={{ mb: 2 }}
      />

      {/* Change list */}
      <Box sx={{ display: 'flex', flexDirection: 'column' }}>
        {query.isFetching && rows.length === 0 ? (
          Array.from({ length: 8 }).map((_, i) => (
            <Box
              key={i}
              sx={{
                py: 1,
                px: 1.5,
                borderBottom: '1px solid',
                borderColor: 'divider',
              }}
            >
              <Box sx={{ display: 'flex', gap: 1.5, alignItems: 'center' }}>
                <Box sx={{ width: 8, height: 8, borderRadius: '50%', bgcolor: 'grey.200' }} />
                <Box sx={{ width: 140, height: 10, borderRadius: 1, bgcolor: 'grey.100' }} />
                <Box sx={{ width: 60, height: 10, borderRadius: 1, bgcolor: 'grey.100' }} />
              </Box>
            </Box>
          ))
        ) : rows.length === 0 ? (
          <Box sx={{ py: 6, textAlign: 'center' }}>
            <CompareArrowsIcon sx={{ fontSize: 32, color: 'grey.300', mb: 0.5 }} />
            <Typography variant="caption" color="text.secondary" display="block">
              No entity changes found
            </Typography>
          </Box>
        ) : (
          rows.map((change, index) => {
            const colors = getOperationColor(change.operation)
            const isLast = index === rows.length - 1
            const propCount = countChangedProperties(change.changedProperties)

            return (
              <ButtonBase
                key={change.id}
                onClick={() => onViewChange?.(change)}
                sx={{
                  py: 0.875,
                  px: 1.5,
                  display: 'flex',
                  alignItems: 'center',
                  gap: 1.5,
                  borderBottom: isLast ? 'none' : '1px solid',
                  borderColor: (theme) => alpha(theme.palette.divider, 0.6),
                  transition: 'background-color 0.1s ease',
                  justifyContent: 'flex-start',
                  width: '100%',
                  '&:hover': {
                    bgcolor: (theme) => alpha(theme.palette.primary.main, 0.02),
                  },
                }}
              >
                {/* Color dot */}
                <Box
                  sx={{
                    width: 7,
                    height: 7,
                    borderRadius: '50%',
                    bgcolor: colors.dot,
                    flexShrink: 0,
                    opacity: 0.8,
                  }}
                />

                {/* Operation */}
                <Typography
                  sx={{
                    fontSize: '0.775rem',
                    fontWeight: 600,
                    color: colors.text,
                    bgcolor: colors.bg,
                    px: 0.875,
                    py: 0.125,
                    borderRadius: 1,
                    lineHeight: 1.4,
                    whiteSpace: 'nowrap',
                    minWidth: 0,
                  }}
                >
                  {change.operation}
                </Typography>

                {/* Entity type */}
                <Typography
                  variant="caption"
                  sx={{
                    color: 'text.secondary',
                    fontSize: '0.75rem',
                    whiteSpace: 'nowrap',
                  }}
                >
                  {change.entityType}
                </Typography>

                {/* Entity ID */}
                {change.entityId && (
                  <Typography
                    variant="caption"
                    sx={{
                      fontFamily: '"SF Mono", "Fira Code", monospace',
                      fontSize: '0.675rem',
                      color: 'text.disabled',
                      whiteSpace: 'nowrap',
                      display: { xs: 'none', sm: 'block' },
                    }}
                  >
                    {change.entityId.substring(0, 8)}
                  </Typography>
                )}

                {/* Changed properties count */}
                {propCount > 0 && (
                  <Typography
                    sx={{
                      fontSize: '0.675rem',
                      fontWeight: 600,
                      color: 'text.disabled',
                      bgcolor: (theme) => alpha(theme.palette.divider, 0.4),
                      px: 0.75,
                      py: 0.125,
                      borderRadius: 0.75,
                      lineHeight: 1.4,
                      whiteSpace: 'nowrap',
                    }}
                  >
                    {propCount} {propCount === 1 ? 'field' : 'fields'}
                  </Typography>
                )}

                {/* Spacer */}
                <Box sx={{ flex: 1 }} />

                {/* Timestamp */}
                <Typography
                  variant="caption"
                  sx={{
                    color: 'text.disabled',
                    fontSize: '0.7rem',
                    fontWeight: 500,
                    whiteSpace: 'nowrap',
                    minWidth: 24,
                    textAlign: 'right',
                  }}
                >
                  {timeAgo(change.occurredAt)}
                </Typography>
              </ButtonBase>
            )
          })
        )}
      </Box>

      {/* Pagination */}
      {totalCount > 0 && (
        <Box
          sx={{
            display: 'flex',
            alignItems: 'center',
            justifyContent: 'space-between',
            mt: 1.5,
            pt: 1.5,
            borderTop: '1px solid',
            borderColor: 'divider',
          }}
        >
          <Typography variant="caption" sx={{ color: 'text.disabled', fontSize: '0.7rem' }}>
            {page * pageSize + 1}--{Math.min((page + 1) * pageSize, totalCount)} of {totalCount}
          </Typography>
          <Box sx={{ display: 'flex', alignItems: 'center', gap: 0.25 }}>
            <IconButton
              size="small"
              disabled={page === 0}
              onClick={() => setPage(p => p - 1)}
              sx={{ p: 0.5 }}
            >
              <NavigateBeforeIcon sx={{ fontSize: 18 }} />
            </IconButton>
            <Typography variant="caption" sx={{ color: 'text.disabled', fontSize: '0.7rem', px: 0.5 }}>
              {page + 1}/{totalPages || 1}
            </Typography>
            <IconButton
              size="small"
              disabled={page >= totalPages - 1}
              onClick={() => setPage(p => p + 1)}
              sx={{ p: 0.5 }}
            >
              <NavigateNextIcon sx={{ fontSize: 18 }} />
            </IconButton>
          </Box>
        </Box>
      )}
    </Box>
  )
}

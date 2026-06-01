import { Box, ButtonBase, TextField, InputAdornment, Typography, IconButton, Checkbox, alpha } from '@mui/material'
import { useMemo, useState, useEffect, useRef, useCallback } from 'react'
import SearchIcon from '@mui/icons-material/Search'
import BugReportIcon from '@mui/icons-material/BugReport'
import NavigateBeforeIcon from '@mui/icons-material/NavigateBefore'
import NavigateNextIcon from '@mui/icons-material/NavigateNext'
import { useGetApiErrorLogs, ErrorLogListItem } from '../../../../../api/queries-commands'
import { formatSwedishDateTime, useDebounce } from '../../../../shared/utils'

interface ErrorLogsTableProps {
  onViewError?: (error: ErrorLogListItem) => void
  sourceFilter?: string
  severityFilter?: string
  resolvedFilter?: string
  selectedIds: Set<string>
  onToggleSelect: (id: string) => void
  onToggleSelectAll: (ids: string[]) => void
}

const sourceColorMap: Record<string, { bg: string; text: string; dot: string }> = {
  HTTP: { bg: 'info.light', text: 'info.dark', dot: 'info.main' },
  Hangfire: { bg: 'warning.light', text: 'warning.dark', dot: 'warning.main' },
  BackgroundService: { bg: 'secondary.light', text: 'secondary.dark', dot: 'secondary.main' },
  Unhandled: { bg: 'error.light', text: 'error.dark', dot: 'error.main' },
  Frontend: { bg: 'primary.light', text: 'primary.dark', dot: 'primary.main' },
}

function getSourceColor(source: string) {
  if (sourceColorMap[source]) return sourceColorMap[source]
  return { bg: 'grey.100', text: 'text.secondary', dot: 'text.disabled' }
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

function truncateMessage(msg: string, maxLen = 80): string {
  if (!msg) return ''
  if (msg.length <= maxLen) return msg
  return msg.substring(0, maxLen) + '...'
}

export function ErrorLogsTable({
  onViewError,
  sourceFilter,
  severityFilter,
  resolvedFilter,
  selectedIds,
  onToggleSelect,
  onToggleSelectAll,
}: ErrorLogsTableProps) {
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

  // Reset page when filters change
  useEffect(() => {
    setPage(0)
  }, [sourceFilter, severityFilter, resolvedFilter])

  const params = useMemo(() => ({
    search: debouncedSearch || undefined,
    page: page + 1,
    pageSize,
    source: sourceFilter || undefined,
    severity: severityFilter || undefined,
    isResolved: resolvedFilter === 'resolved' ? true : resolvedFilter === 'unresolved' ? false : undefined,
  }), [debouncedSearch, page, sourceFilter, severityFilter, resolvedFilter])

  const query = useGetApiErrorLogs(params, { query: { staleTime: 30000 } })
  const rows = query.data?.items ?? []
  const totalCount = query.data?.totalCount ?? 0
  const totalPages = Math.ceil(totalCount / pageSize)

  const visibleIds = useMemo(() => rows.map((r) => r.id), [rows])
  const allVisibleSelected = visibleIds.length > 0 && visibleIds.every((id) => selectedIds.has(id))
  const someVisibleSelected = visibleIds.some((id) => selectedIds.has(id)) && !allVisibleSelected

  const handleSelectAllClick = useCallback(() => {
    onToggleSelectAll(visibleIds)
  }, [visibleIds, onToggleSelectAll])

  return (
    <Box>
      {/* Search + select all header */}
      <Box sx={{ display: 'flex', alignItems: 'center', gap: 1, mb: 2 }}>
        <Checkbox
          size="small"
          checked={allVisibleSelected}
          indeterminate={someVisibleSelected}
          onChange={handleSelectAllClick}
          disabled={rows.length === 0}
          sx={{
            p: 0.5,
            '& .MuiSvgIcon-root': { fontSize: 20 },
          }}
        />
        <TextField
          fullWidth
          size="small"
          placeholder="Search errors..."
          value={searchValue}
          onChange={(e) => setSearchValue(e.target.value)}
          InputProps={{
            startAdornment: (
              <InputAdornment position="start">
                <SearchIcon sx={{ color: 'text.disabled', fontSize: 18 }} />
              </InputAdornment>
            ),
          }}
        />
      </Box>

      {/* Error list */}
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
                <Box sx={{ width: 60, height: 10, borderRadius: 1, bgcolor: 'grey.100' }} />
                <Box sx={{ width: 200, height: 10, borderRadius: 1, bgcolor: 'grey.100' }} />
                <Box sx={{ width: 50, height: 10, borderRadius: 1, bgcolor: 'grey.100' }} />
              </Box>
            </Box>
          ))
        ) : rows.length === 0 ? (
          <Box sx={{ py: 6, textAlign: 'center' }}>
            <BugReportIcon sx={{ fontSize: 32, color: 'grey.300', mb: 0.5 }} />
            <Typography variant="caption" color="text.secondary" display="block">
              No errors found
            </Typography>
          </Box>
        ) : (
          rows.map((error, index) => {
            const colors = getSourceColor(error.source)
            const isLast = index === rows.length - 1
            const isSelected = selectedIds.has(error.id)

            return (
              <Box
                key={error.id}
                sx={{
                  display: 'flex',
                  alignItems: 'center',
                  borderBottom: isLast ? 'none' : '1px solid',
                  borderColor: (theme) => alpha(theme.palette.divider, 0.6),
                  bgcolor: isSelected
                    ? (theme) => alpha(theme.palette.primary.main, 0.06)
                    : 'transparent',
                  transition: 'background-color 0.15s ease',
                  '&:hover': {
                    bgcolor: isSelected
                      ? (theme) => alpha(theme.palette.primary.main, 0.09)
                      : (theme) => alpha(theme.palette.primary.main, 0.02),
                  },
                }}
              >
                {/* Checkbox area */}
                <Box
                  onClick={(e) => {
                    e.stopPropagation()
                    onToggleSelect(error.id)
                  }}
                  sx={{
                    display: 'flex',
                    alignItems: 'center',
                    justifyContent: 'center',
                    pl: 0.5,
                    pr: 0,
                    py: 0.875,
                    cursor: 'pointer',
                    flexShrink: 0,
                  }}
                >
                  <Checkbox
                    size="small"
                    checked={isSelected}
                    tabIndex={-1}
                    sx={{
                      p: 0.5,
                      '& .MuiSvgIcon-root': { fontSize: 18 },
                    }}
                  />
                </Box>

                {/* Row content - clickable for detail dialog */}
                <ButtonBase
                  onClick={() => onViewError?.(error)}
                  sx={{
                    py: 0.875,
                    px: 1,
                    display: 'flex',
                    alignItems: 'center',
                    gap: 1.5,
                    transition: 'background-color 0.1s ease',
                    justifyContent: 'flex-start',
                    flex: 1,
                    minWidth: 0,
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

                  {/* Source chip */}
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
                    {error.source}
                  </Typography>

                  {/* Message (truncated) */}
                  <Typography
                    variant="caption"
                    sx={{
                      color: 'text.secondary',
                      fontSize: '0.75rem',
                      whiteSpace: 'nowrap',
                      overflow: 'hidden',
                      textOverflow: 'ellipsis',
                      minWidth: 0,
                      flex: '0 1 auto',
                    }}
                  >
                    {truncateMessage(error.message)}
                  </Typography>

                  {/* Severity chip */}
                  <Typography
                    sx={{
                      fontSize: '0.675rem',
                      fontWeight: 600,
                      color: error.severity === 'Critical' ? 'error.dark' : error.severity === 'Warning' ? 'info.dark' : 'warning.dark',
                      bgcolor: error.severity === 'Critical' ? 'error.light' : error.severity === 'Warning' ? 'info.light' : 'warning.light',
                      px: 0.75,
                      py: 0.125,
                      borderRadius: 0.75,
                      lineHeight: 1.4,
                      whiteSpace: 'nowrap',
                      flexShrink: 0,
                    }}
                  >
                    {error.severity}
                  </Typography>

                  {/* Resolved indicator */}
                  {error.isResolved && (
                    <Typography
                      sx={{
                        fontSize: '0.675rem',
                        fontWeight: 600,
                        color: 'success.dark',
                        bgcolor: 'success.light',
                        px: 0.75,
                        py: 0.125,
                        borderRadius: 0.75,
                        lineHeight: 1.4,
                        whiteSpace: 'nowrap',
                        flexShrink: 0,
                        display: { xs: 'none', sm: 'block' },
                      }}
                    >
                      Resolved
                    </Typography>
                  )}
                  {!error.isResolved && (
                    <Typography
                      sx={{
                        fontSize: '0.675rem',
                        fontWeight: 500,
                        color: 'text.disabled',
                        bgcolor: (theme) => alpha(theme.palette.divider, 0.4),
                        px: 0.75,
                        py: 0.125,
                        borderRadius: 0.75,
                        lineHeight: 1.4,
                        whiteSpace: 'nowrap',
                        flexShrink: 0,
                        display: { xs: 'none', sm: 'block' },
                      }}
                    >
                      Unresolved
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
                    {timeAgo(error.createdAt)}
                  </Typography>
                </ButtonBase>
              </Box>
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

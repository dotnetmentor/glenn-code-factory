import {
  Box,
  Dialog,
  DialogContent,
  IconButton,
  Typography,
  Chip,
  Slide,
  Button,
  Paper,
  Grow,
  Snackbar,
  Alert,
  alpha,
} from '@mui/material'
import { TransitionProps } from '@mui/material/transitions'
import { forwardRef, useState, useCallback, useEffect } from 'react'
import CloseIcon from '@mui/icons-material/Close'
import BugReportIcon from '@mui/icons-material/BugReport'
import ContentCopyIcon from '@mui/icons-material/ContentCopy'
import CheckCircleOutlineIcon from '@mui/icons-material/CheckCircleOutline'
import CheckCircleIcon from '@mui/icons-material/CheckCircle'
import HighlightOffIcon from '@mui/icons-material/HighlightOff'
import ClearIcon from '@mui/icons-material/Clear'
import { useQueryClient } from '@tanstack/react-query'
import { ErrorLogsTable } from '../components/ErrorLogsTable'
import {
  ErrorLogListItem,
  useGetApiErrorLogsId,
  usePutApiErrorLogsId,
  usePutApiErrorLogsBulkResolve,
  getGetApiErrorLogsQueryKey,
  getGetApiErrorLogsIdQueryKey,
  getGetApiErrorLogsCountQueryKey,
} from '../../../../../api/queries-commands'
import { formatSwedishDateTime } from '../../../../shared/utils'

const SlideUp = forwardRef(function Transition(
  props: TransitionProps & { children: React.ReactElement },
  ref: React.Ref<unknown>,
) {
  return <Slide direction="up" ref={ref} {...props} />
})

const sourceChipColor: Record<string, 'info' | 'warning' | 'secondary' | 'error' | 'primary' | 'default'> = {
  HTTP: 'info',
  Hangfire: 'warning',
  BackgroundService: 'secondary',
  Unhandled: 'error',
  Frontend: 'primary',
}

const severityChipColor: Record<string, 'warning' | 'error' | 'info' | 'default'> = {
  Warning: 'info',
  Error: 'warning',
  Critical: 'error',
}

function MetaItem({ label, value, mono }: { label: string; value: string; mono?: boolean }) {
  return (
    <Box>
      <Typography
        variant="overline"
        sx={{ fontSize: '0.65rem', color: 'text.disabled', letterSpacing: '0.08em', lineHeight: 1 }}
      >
        {label}
      </Typography>
      <Typography
        variant="body2"
        sx={{
          mt: 0.25,
          fontWeight: 500,
          fontSize: '0.825rem',
          color: 'text.primary',
          ...(mono && {
            fontFamily: '"SF Mono", "Fira Code", "Consolas", monospace',
            fontSize: '0.775rem',
            wordBreak: 'break-all',
          }),
        }}
      >
        {value || '\u2014'}
      </Typography>
    </Box>
  )
}

export function ErrorLogsPage() {
  const [selectedError, setSelectedError] = useState<ErrorLogListItem | null>(null)
  const [copiedField, setCopiedField] = useState<string | null>(null)
  const [sourceFilter, setSourceFilter] = useState<string>('')
  const [severityFilter, setSeverityFilter] = useState<string>('')
  const [resolvedFilter, setResolvedFilter] = useState<string>('')

  // Multi-select state
  const [selectedIds, setSelectedIds] = useState<Set<string>>(new Set())
  const [snackbar, setSnackbar] = useState<{ open: boolean; message: string }>({ open: false, message: '' })

  const queryClient = useQueryClient()

  // Clear selection when filters change
  useEffect(() => {
    setSelectedIds(new Set())
  }, [sourceFilter, severityFilter, resolvedFilter])

  const detailsQuery = useGetApiErrorLogsId(selectedError?.id ?? '', {
    query: { enabled: !!selectedError?.id },
  })
  const errorDetails = detailsQuery.data

  const resolveMutation = usePutApiErrorLogsId({
    mutation: {
      onSuccess: () => {
        queryClient.invalidateQueries({ queryKey: getGetApiErrorLogsQueryKey() })
        queryClient.invalidateQueries({ queryKey: getGetApiErrorLogsCountQueryKey() })
        if (selectedError?.id) {
          queryClient.invalidateQueries({ queryKey: getGetApiErrorLogsIdQueryKey(selectedError.id) })
        }
      },
    },
  })

  const bulkResolveMutation = usePutApiErrorLogsBulkResolve({
    mutation: {
      onSuccess: (data, variables) => {
        const count = data.updatedCount
        const action = variables.data.isResolved ? 'resolved' : 'marked as unresolved'
        setSnackbar({
          open: true,
          message: `${count} error${count !== 1 ? 's' : ''} ${action}`,
        })
        setSelectedIds(new Set())
        queryClient.invalidateQueries({ queryKey: getGetApiErrorLogsQueryKey() })
        queryClient.invalidateQueries({ queryKey: getGetApiErrorLogsCountQueryKey() })
      },
    },
  })

  const handleViewError = (error: ErrorLogListItem) => {
    setSelectedError(error)
  }

  const handleCloseError = () => {
    setSelectedError(null)
  }

  const handleCopy = (text: string, field: string) => {
    navigator.clipboard.writeText(text)
    setCopiedField(field)
    setTimeout(() => setCopiedField(null), 2000)
  }

  const handleToggleResolved = () => {
    if (!errorDetails || !selectedError) return
    const newResolved = !errorDetails.isResolved
    resolveMutation.mutate({
      id: selectedError.id,
      data: {
        isResolved: newResolved,
        resolvedAt: newResolved ? new Date().toISOString() : null,
      },
    })
  }

  // Multi-select handlers
  const handleToggleSelect = useCallback((id: string) => {
    setSelectedIds((prev) => {
      const next = new Set(prev)
      if (next.has(id)) {
        next.delete(id)
      } else {
        next.add(id)
      }
      return next
    })
  }, [])

  const handleToggleSelectAll = useCallback((visibleIds: string[]) => {
    setSelectedIds((prev) => {
      const allSelected = visibleIds.every((id) => prev.has(id))
      if (allSelected) {
        // Deselect all visible
        const next = new Set(prev)
        visibleIds.forEach((id) => next.delete(id))
        return next
      } else {
        // Select all visible
        const next = new Set(prev)
        visibleIds.forEach((id) => next.add(id))
        return next
      }
    })
  }, [])

  const handleClearSelection = useCallback(() => {
    setSelectedIds(new Set())
  }, [])

  const handleBulkResolve = useCallback((isResolved: boolean) => {
    const ids = Array.from(selectedIds)
    if (ids.length === 0) return
    bulkResolveMutation.mutate({
      data: { ids, isResolved },
    })
  }, [selectedIds, bulkResolveMutation])

  const selectionCount = selectedIds.size

  return (
    <>
      {/* Page header */}
      <Box sx={{ mb: 4 }}>
        <Box sx={{ display: 'flex', alignItems: 'center', gap: 1.5, mb: 0.5 }}>
          <Box
            sx={{
              width: 36,
              height: 36,
              borderRadius: 2,
              bgcolor: (theme) => alpha(theme.palette.error.main, 0.06),
              border: '1px solid',
              borderColor: 'divider',
              display: 'flex',
              alignItems: 'center',
              justifyContent: 'center',
            }}
          >
            <BugReportIcon sx={{ fontSize: 18, color: 'text.secondary' }} />
          </Box>
          <Box>
            <Typography variant="h4" component="h1" sx={{ lineHeight: 1.2 }}>
              Error Log
            </Typography>
            <Typography variant="caption" color="text.secondary">
              Track and investigate application errors
            </Typography>
          </Box>
        </Box>
      </Box>

      {/* Filter bar */}
      <Box sx={{ display: 'flex', gap: 1.5, mb: 2, flexWrap: 'wrap' }}>
        {/* Source filter */}
        <Box sx={{ display: 'flex', gap: 0.5, alignItems: 'center' }}>
          <Typography variant="caption" sx={{ color: 'text.disabled', fontSize: '0.7rem', mr: 0.25 }}>
            Source
          </Typography>
          {['', 'HTTP', 'Hangfire', 'BackgroundService', 'Unhandled', 'Frontend'].map((src) => (
            <Chip
              key={src || 'all'}
              label={src || 'All'}
              size="small"
              variant={sourceFilter === src ? 'filled' : 'outlined'}
              color={sourceFilter === src ? (src ? sourceChipColor[src] || 'default' : 'default') : 'default'}
              onClick={() => setSourceFilter(src)}
              sx={{
                height: 24,
                fontSize: '0.7rem',
                fontWeight: 500,
                cursor: 'pointer',
              }}
            />
          ))}
        </Box>

        {/* Severity filter */}
        <Box sx={{ display: 'flex', gap: 0.5, alignItems: 'center' }}>
          <Typography variant="caption" sx={{ color: 'text.disabled', fontSize: '0.7rem', mr: 0.25 }}>
            Severity
          </Typography>
          {['', 'Warning', 'Error', 'Critical'].map((sev) => (
            <Chip
              key={sev || 'all'}
              label={sev || 'All'}
              size="small"
              variant={severityFilter === sev ? 'filled' : 'outlined'}
              color={severityFilter === sev ? (sev ? severityChipColor[sev] || 'default' : 'default') : 'default'}
              onClick={() => setSeverityFilter(sev)}
              sx={{
                height: 24,
                fontSize: '0.7rem',
                fontWeight: 500,
                cursor: 'pointer',
              }}
            />
          ))}
        </Box>

        {/* Resolved filter */}
        <Box sx={{ display: 'flex', gap: 0.5, alignItems: 'center' }}>
          <Typography variant="caption" sx={{ color: 'text.disabled', fontSize: '0.7rem', mr: 0.25 }}>
            Status
          </Typography>
          {[
            { value: '', label: 'All' },
            { value: 'unresolved', label: 'Unresolved' },
            { value: 'resolved', label: 'Resolved' },
          ].map((opt) => (
            <Chip
              key={opt.value || 'all'}
              label={opt.label}
              size="small"
              variant={resolvedFilter === opt.value ? 'filled' : 'outlined'}
              color={resolvedFilter === opt.value && opt.value ? (opt.value === 'resolved' ? 'success' : 'default') : 'default'}
              onClick={() => setResolvedFilter(opt.value)}
              sx={{
                height: 24,
                fontSize: '0.7rem',
                fontWeight: 500,
                cursor: 'pointer',
              }}
            />
          ))}
        </Box>
      </Box>

      {/* Content card */}
      <Box
        sx={{
          bgcolor: 'background.paper',
          borderRadius: 3,
          border: '1px solid',
          borderColor: 'divider',
          overflow: 'hidden',
          p: { xs: 2, sm: 3 },
          // Add bottom padding when floating bar is visible so it doesn't overlap pagination
          pb: selectionCount > 0 ? { xs: 10, sm: 10 } : { xs: 2, sm: 3 },
          transition: 'padding-bottom 0.3s ease',
        }}
      >
        <ErrorLogsTable
          onViewError={handleViewError}
          sourceFilter={sourceFilter}
          severityFilter={severityFilter}
          resolvedFilter={resolvedFilter}
          selectedIds={selectedIds}
          onToggleSelect={handleToggleSelect}
          onToggleSelectAll={handleToggleSelectAll}
        />
      </Box>

      {/* Floating bulk action bar */}
      <Grow in={selectionCount > 0} unmountOnExit>
        <Paper
          elevation={8}
          sx={{
            position: 'fixed',
            bottom: 32,
            left: '50%',
            transform: 'translateX(-50%)',
            zIndex: (theme) => theme.zIndex.snackbar,
            display: 'flex',
            alignItems: 'center',
            gap: 2,
            px: 3,
            py: 1.5,
            borderRadius: 3,
            bgcolor: 'background.paper',
            border: '1px solid',
            borderColor: 'divider',
            minWidth: 360,
          }}
        >
          <Typography
            variant="body2"
            sx={{
              fontWeight: 600,
              fontSize: '0.875rem',
              color: 'text.primary',
              whiteSpace: 'nowrap',
            }}
          >
            {selectionCount} selected
          </Typography>

          <Box sx={{ display: 'flex', gap: 1, ml: 'auto' }}>
            <Button
              variant="contained"
              color="success"
              size="small"
              startIcon={<CheckCircleIcon />}
              onClick={() => handleBulkResolve(true)}
              disabled={bulkResolveMutation.isPending}
              sx={{
                borderRadius: 2,
                textTransform: 'none',
                fontWeight: 600,
                fontSize: '0.8rem',
                boxShadow: 'none',
                '&:hover': { boxShadow: 'none' },
              }}
            >
              {bulkResolveMutation.isPending ? 'Updating...' : 'Mark as Resolved'}
            </Button>
            <Button
              variant="outlined"
              size="small"
              onClick={() => handleBulkResolve(false)}
              disabled={bulkResolveMutation.isPending}
              sx={{
                borderRadius: 2,
                textTransform: 'none',
                fontWeight: 500,
                fontSize: '0.8rem',
              }}
            >
              Mark as Unresolved
            </Button>
            <Button
              variant="text"
              size="small"
              startIcon={<ClearIcon />}
              onClick={handleClearSelection}
              sx={{
                borderRadius: 2,
                textTransform: 'none',
                fontWeight: 500,
                fontSize: '0.8rem',
                color: 'text.secondary',
              }}
            >
              Clear
            </Button>
          </Box>
        </Paper>
      </Grow>

      {/* Success snackbar */}
      <Snackbar
        open={snackbar.open}
        autoHideDuration={4000}
        onClose={() => setSnackbar({ open: false, message: '' })}
        anchorOrigin={{ vertical: 'bottom', horizontal: 'center' }}
      >
        <Alert
          onClose={() => setSnackbar({ open: false, message: '' })}
          severity="success"
          variant="filled"
          sx={{ borderRadius: 2, fontWeight: 500 }}
        >
          {snackbar.message}
        </Alert>
      </Snackbar>

      {/* Error detail dialog */}
      <Dialog
        open={!!selectedError}
        onClose={handleCloseError}
        TransitionComponent={SlideUp}
        maxWidth="sm"
        fullWidth
        PaperProps={{
          sx: {
            borderRadius: 3,
            maxHeight: '85vh',
          },
        }}
      >
        {selectedError && (
          <DialogContent sx={{ p: 0 }}>
            {/* Header */}
            <Box
              sx={{
                px: 3,
                pt: 2.5,
                pb: 2,
                display: 'flex',
                alignItems: 'flex-start',
                justifyContent: 'space-between',
                borderBottom: '1px solid',
                borderColor: 'divider',
              }}
            >
              <Box sx={{ display: 'flex', alignItems: 'center', gap: 1, flexWrap: 'wrap' }}>
                <Chip
                  label={errorDetails?.source ?? selectedError.source}
                  size="small"
                  color={sourceChipColor[(errorDetails?.source ?? selectedError.source)] || 'default'}
                  sx={{ fontWeight: 600, fontSize: '0.775rem', height: 26 }}
                />
                <Chip
                  label={errorDetails?.severity ?? selectedError.severity}
                  size="small"
                  color={severityChipColor[(errorDetails?.severity ?? selectedError.severity)] || 'default'}
                  sx={{ fontWeight: 600, fontSize: '0.775rem', height: 26 }}
                />
                <Chip
                  label={errorDetails?.isResolved ? 'Resolved' : 'Unresolved'}
                  size="small"
                  color={errorDetails?.isResolved ? 'success' : 'default'}
                  variant={errorDetails?.isResolved ? 'filled' : 'outlined'}
                  sx={{ fontWeight: 500, fontSize: '0.725rem', height: 24 }}
                />
              </Box>
              <IconButton size="small" onClick={handleCloseError} sx={{ mt: -0.5 }}>
                <CloseIcon fontSize="small" />
              </IconButton>
            </Box>

            {/* Meta grid */}
            <Box
              sx={{
                px: 3,
                py: 2.5,
                display: 'grid',
                gridTemplateColumns: '1fr 1fr',
                gap: 2.5,
                borderBottom: '1px solid',
                borderColor: 'divider',
                bgcolor: (theme) => alpha(theme.palette.background.default, 0.5),
              }}
            >
              <MetaItem
                label="Occurred At"
                value={formatSwedishDateTime(errorDetails?.createdAt ?? selectedError.createdAt) || (errorDetails?.createdAt ?? selectedError.createdAt)}
              />
              <Box>
                <Typography
                  variant="overline"
                  sx={{ fontSize: '0.65rem', color: 'text.disabled', letterSpacing: '0.08em', lineHeight: 1 }}
                >
                  Correlation ID
                </Typography>
                <Box sx={{ display: 'flex', alignItems: 'center', gap: 0.5, mt: 0.25 }}>
                  <Typography
                    variant="body2"
                    sx={{
                      fontWeight: 500,
                      fontSize: '0.775rem',
                      fontFamily: '"SF Mono", "Fira Code", "Consolas", monospace',
                      wordBreak: 'break-all',
                      color: 'text.primary',
                    }}
                  >
                    {errorDetails?.correlationId || '\u2014'}
                  </Typography>
                  {errorDetails?.correlationId && (
                    <IconButton
                      size="small"
                      onClick={() => handleCopy(errorDetails.correlationId!, 'correlationId')}
                      sx={{ opacity: 0.4, '&:hover': { opacity: 1 }, p: 0.25 }}
                    >
                      <ContentCopyIcon sx={{ fontSize: 12 }} />
                    </IconButton>
                  )}
                </Box>
                {copiedField === 'correlationId' && (
                  <Typography variant="caption" sx={{ color: 'success.main', fontSize: '0.65rem' }}>
                    Copied
                  </Typography>
                )}
              </Box>
              {(errorDetails?.requestPath || selectedError.requestPath) && (
                <MetaItem
                  label="Request Path"
                  value={`${errorDetails?.requestMethod ?? ''} ${errorDetails?.requestPath ?? selectedError.requestPath ?? ''}`}
                  mono
                />
              )}
              {errorDetails?.contextData && (
                <MetaItem label="Context Data" value={errorDetails.contextData} />
              )}
            </Box>

            {/* Message */}
            <Box sx={{ px: 3, py: 2, borderBottom: '1px solid', borderColor: 'divider' }}>
              <Typography
                variant="overline"
                sx={{ fontSize: '0.65rem', color: 'text.disabled', letterSpacing: '0.08em', mb: 0.5, display: 'block' }}
              >
                Message
              </Typography>
              <Typography
                variant="body2"
                sx={{ fontWeight: 500, fontSize: '0.825rem', color: 'text.primary', lineHeight: 1.5 }}
              >
                {errorDetails?.message ?? selectedError.message}
              </Typography>
            </Box>

            {/* Stack trace */}
            {errorDetails?.stackTrace && (
              <Box sx={{ px: 3, py: 2.5 }}>
                <Box sx={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', mb: 1.5 }}>
                  <Typography
                    variant="overline"
                    sx={{ fontSize: '0.65rem', color: 'text.disabled', letterSpacing: '0.08em' }}
                  >
                    Stack Trace
                  </Typography>
                  <IconButton
                    size="small"
                    onClick={() => handleCopy(errorDetails.stackTrace!, 'stackTrace')}
                    sx={{ opacity: 0.5, '&:hover': { opacity: 1 } }}
                  >
                    <ContentCopyIcon sx={{ fontSize: 14 }} />
                  </IconButton>
                </Box>
                {copiedField === 'stackTrace' && (
                  <Typography variant="caption" sx={{ color: 'success.main', mb: 1, display: 'block' }}>
                    Copied to clipboard
                  </Typography>
                )}
                <Box
                  component="pre"
                  sx={{
                    p: 2,
                    m: 0,
                    borderRadius: 2,
                    bgcolor: 'grey.900',
                    color: 'grey.100',
                    overflow: 'auto',
                    maxHeight: 400,
                    fontSize: '0.75rem',
                    lineHeight: 1.6,
                    fontFamily: '"SF Mono", "Fira Code", "Consolas", monospace',
                    border: '1px solid',
                    borderColor: 'rgba(255,255,255,0.06)',
                    whiteSpace: 'pre-wrap',
                    wordBreak: 'break-all',
                    '&::-webkit-scrollbar': {
                      width: 6,
                      height: 6,
                    },
                    '&::-webkit-scrollbar-track': {
                      background: 'transparent',
                    },
                    '&::-webkit-scrollbar-thumb': {
                      background: 'rgba(255,255,255,0.15)',
                      borderRadius: 3,
                    },
                  }}
                >
                  {errorDetails.stackTrace}
                </Box>
              </Box>
            )}

            {/* Action bar */}
            <Box
              sx={{
                px: 3,
                py: 2,
                display: 'flex',
                justifyContent: 'flex-end',
                gap: 1,
                borderTop: '1px solid',
                borderColor: 'divider',
              }}
            >
              <Button
                variant={errorDetails?.isResolved ? 'outlined' : 'contained'}
                color={errorDetails?.isResolved ? 'warning' : 'success'}
                size="small"
                startIcon={errorDetails?.isResolved ? <HighlightOffIcon /> : <CheckCircleOutlineIcon />}
                onClick={handleToggleResolved}
                disabled={resolveMutation.isPending}
                sx={{
                  borderRadius: 2,
                  textTransform: 'none',
                  fontWeight: 600,
                  fontSize: '0.8rem',
                  boxShadow: 'none',
                  '&:hover': { boxShadow: 'none' },
                }}
              >
                {resolveMutation.isPending
                  ? 'Updating...'
                  : errorDetails?.isResolved
                    ? 'Mark as Unresolved'
                    : 'Mark as Resolved'}
              </Button>
              <Button
                variant="outlined"
                size="small"
                onClick={handleCloseError}
                sx={{
                  borderRadius: 2,
                  textTransform: 'none',
                  fontWeight: 500,
                  fontSize: '0.8rem',
                }}
              >
                Close
              </Button>
            </Box>
          </DialogContent>
        )}
      </Dialog>
    </>
  )
}

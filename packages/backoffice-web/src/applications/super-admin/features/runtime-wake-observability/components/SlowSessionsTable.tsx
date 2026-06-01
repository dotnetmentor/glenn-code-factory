import {
  Alert,
  Box,
  Chip,
  CircularProgress,
  Paper,
  Skeleton,
  Stack,
  Table,
  TableBody,
  TableCell,
  TableContainer,
  TableHead,
  TableRow,
  Typography,
} from '@mui/material'
import type { SlowWakeSession } from '@/api/queries-commands'
import { formatDuration } from '@/applications/super-admin/features/project-runtime'

interface SlowSessionsTableProps {
  sessions: SlowWakeSession[] | undefined
  isLoading: boolean
  error: unknown
  onSelect: (session: SlowWakeSession) => void
}

const COLUMNS = ['Project / Branch / Region', 'Started at (UTC)', 'Duration', 'Dominant stage']
const COLUMN_COUNT = COLUMNS.length

/**
 * Render an ISO instant as a compact "YYYY-MM-DD HH:MM:SS UTC" so operators
 * can correlate against logs without doing timezone math in their head. We
 * keep this explicitly UTC — the table is a fleet-wide triage surface so a
 * shared anchor matters more than each viewer's local time.
 */
function formatStartedAtUtc(iso: string): string {
  const d = new Date(iso)
  if (Number.isNaN(d.getTime())) return iso
  const pad = (n: number) => String(n).padStart(2, '0')
  const yyyy = d.getUTCFullYear()
  const mm = pad(d.getUTCMonth() + 1)
  const dd = pad(d.getUTCDate())
  const hh = pad(d.getUTCHours())
  const mi = pad(d.getUTCMinutes())
  const ss = pad(d.getUTCSeconds())
  return `${yyyy}-${mm}-${dd} ${hh}:${mi}:${ss} UTC`
}

function shortId(id: string): string {
  return id.length > 8 ? id.slice(0, 8) : id
}

export function SlowSessionsTable({
  sessions,
  isLoading,
  error,
  onSelect,
}: SlowSessionsTableProps) {
  return (
    <Paper variant="outlined" sx={{ p: 3 }}>
      <Stack spacing={1.5}>
        <Box>
          <Typography variant="h6">Slow sessions</Typography>
          <Typography variant="caption" color="text.secondary">
            Slowest recent wakes — click a row to open the timeline drawer.
          </Typography>
        </Box>

        {error instanceof Error && (
          <Alert severity="error">
            Failed to load slow sessions: {error.message}
          </Alert>
        )}

        <TableContainer>
          <Table size="small">
            <TableHead>
              <TableRow>
                {COLUMNS.map((c) => (
                  <TableCell key={c}>{c}</TableCell>
                ))}
              </TableRow>
            </TableHead>
            <TableBody>
              {isLoading &&
                Array.from({ length: 4 }).map((_, i) => (
                  <TableRow key={`skel-${i}`}>
                    {Array.from({ length: COLUMN_COUNT }).map((__, j) => (
                      <TableCell key={j}>
                        <Skeleton width="80%" />
                      </TableCell>
                    ))}
                  </TableRow>
                ))}

              {!isLoading && !error && (sessions?.length ?? 0) === 0 && (
                <TableRow>
                  <TableCell colSpan={COLUMN_COUNT}>
                    <Box sx={{ textAlign: 'center', py: 4 }}>
                      <Typography variant="body2" color="text.secondary">
                        No slow sessions in this window.
                      </Typography>
                    </Box>
                  </TableCell>
                </TableRow>
              )}

              {!isLoading &&
                sessions?.map((session) => (
                  <TableRow
                    key={session.runtimeId}
                    hover
                    onClick={() => onSelect(session)}
                    sx={{ cursor: 'pointer' }}
                  >
                    <TableCell>
                      <Stack direction="row" spacing={1} alignItems="center" flexWrap="wrap">
                        <Typography variant="body2" sx={{ fontFamily: 'monospace' }}>
                          {shortId(session.projectId)}
                        </Typography>
                        <Typography variant="body2" color="text.secondary">
                          /
                        </Typography>
                        <Typography variant="body2" sx={{ fontFamily: 'monospace' }}>
                          {shortId(session.branchId)}
                        </Typography>
                        <Chip size="small" label={session.region} variant="outlined" />
                      </Stack>
                    </TableCell>
                    <TableCell>
                      <Typography variant="body2" sx={{ fontVariantNumeric: 'tabular-nums' }}>
                        {formatStartedAtUtc(session.startedAt)}
                      </Typography>
                    </TableCell>
                    <TableCell>
                      <Typography
                        variant="body2"
                        sx={{
                          fontVariantNumeric: 'tabular-nums',
                          color:
                            session.durationMs >= 30_000
                              ? 'error.main'
                              : session.durationMs >= 10_000
                              ? 'warning.main'
                              : 'text.primary',
                        }}
                      >
                        {formatDuration(session.durationMs)}
                      </Typography>
                    </TableCell>
                    <TableCell>
                      {session.dominantStageName ? (
                        <Chip
                          size="small"
                          label={session.dominantStageName}
                          variant="outlined"
                        />
                      ) : (
                        <Typography variant="body2" color="text.disabled">
                          —
                        </Typography>
                      )}
                    </TableCell>
                  </TableRow>
                ))}
            </TableBody>
          </Table>
        </TableContainer>

        {isLoading && (
          <Stack direction="row" spacing={1} alignItems="center">
            <CircularProgress size={12} />
            <Typography variant="caption" color="text.secondary">
              Loading sessions…
            </Typography>
          </Stack>
        )}
      </Stack>
    </Paper>
  )
}

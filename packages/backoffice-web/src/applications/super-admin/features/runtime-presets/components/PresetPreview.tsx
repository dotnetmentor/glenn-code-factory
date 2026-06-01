import { useMemo, useState } from 'react'
import {
  Alert,
  Box,
  Button,
  CircularProgress,
  Divider,
  Stack,
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableRow,
  TextField,
  Typography,
} from '@mui/material'
import PlayArrowIcon from '@mui/icons-material/PlayArrow'
import {
  PresetParameterType,
  usePostApiAdminRuntimePresetsIdPreview,
  type PresetParameter,
  type PreviewPresetResponse,
} from '@/api/queries-commands'

export interface PresetPreviewProps {
  /** Saved preset id. Required to call the preview endpoint. */
  presetId?: string
  /** Parameters from the form (live editing state) so the preview values panel matches. */
  parameters: PresetParameter[]
  /** Sample values the user is currently filling in. */
  parameterValues: Record<string, string>
  /** Notify parent on values changes (so form state can be shared / persisted). */
  onParameterValuesChange: (next: Record<string, string>) => void
}

const MONO_SX = { fontFamily: 'monospace', fontSize: 13 } as const

/**
 * Live-render preview for a Service Preset. Lets the operator type sample
 * values for each defined {@link PresetParameter}, fires the
 * <c>/api/admin/runtime-presets/{id}/preview</c> endpoint, and shows the
 * rendered command / env / contributions as code blocks. Surfaces the
 * server-side validation errors (if any) above the rendered output.
 */
export function PresetPreview({
  presetId,
  parameters,
  parameterValues,
  onParameterValuesChange,
}: PresetPreviewProps) {
  const [lastResponse, setLastResponse] = useState<PreviewPresetResponse | null>(
    null,
  )
  const [requestError, setRequestError] = useState<string | null>(null)
  const previewMutation = usePostApiAdminRuntimePresetsIdPreview()

  const valuesPayload = useMemo(() => {
    const out: Record<string, string | number | boolean> = {}
    for (const param of parameters) {
      const raw = parameterValues[param.key]
      if (raw === undefined || raw === null || raw === '') continue
      out[param.key] = coerceValue(param.type, raw)
    }
    return out
  }, [parameters, parameterValues])

  const handleRefresh = () => {
    if (!presetId) return
    setRequestError(null)
    previewMutation.mutate(
      { id: presetId, data: { values: valuesPayload } },
      {
        onSuccess: (data) => {
          setLastResponse(data)
        },
        onError: (err) => {
          setRequestError(
            err instanceof Error
              ? err.message
              : 'Failed to render preview. Try again.',
          )
        },
      },
    )
  }

  const setValue = (key: string, raw: string) => {
    onParameterValuesChange({ ...parameterValues, [key]: raw })
  }

  if (!presetId) {
    return (
      <Alert severity="info">
        Save the preset to enable live preview. The preview endpoint renders
        the current saved template — drafts that haven't been saved yet
        aren't reachable.
      </Alert>
    )
  }

  return (
    <Stack spacing={2}>
      <Box>
        <Typography variant="subtitle2" sx={{ mb: 1 }}>
          Sample values
        </Typography>
        {parameters.length === 0 && (
          <Typography variant="body2" color="text.secondary">
            No parameters defined yet — preview will render the template as-is.
          </Typography>
        )}
        <Stack spacing={1.5}>
          {parameters.map((param) => (
            <TextField
              key={param.key || param.label}
              label={param.label || param.key}
              value={parameterValues[param.key] ?? ''}
              onChange={(e) => setValue(param.key, e.target.value)}
              size="small"
              fullWidth
              placeholder={param.defaultValue ?? ''}
              helperText={param.description ?? undefined}
              slotProps={{ input: { sx: MONO_SX } }}
            />
          ))}
        </Stack>
      </Box>

      <Box>
        <Button
          variant="contained"
          startIcon={
            previewMutation.isPending ? (
              <CircularProgress size={14} color="inherit" />
            ) : (
              <PlayArrowIcon />
            )
          }
          onClick={handleRefresh}
          disabled={previewMutation.isPending}
          sx={{ textTransform: 'none' }}
        >
          {previewMutation.isPending ? 'Rendering…' : 'Refresh preview'}
        </Button>
      </Box>

      {requestError && <Alert severity="error">{requestError}</Alert>}

      {lastResponse && (
        <>
          {lastResponse.errors.length > 0 && (
            <Alert severity="warning">
              <Stack spacing={0.5}>
                {lastResponse.errors.map((err, i) => (
                  <Typography key={i} variant="body2">
                    {err}
                  </Typography>
                ))}
              </Stack>
            </Alert>
          )}

          <Divider />

          <Section title="Rendered command">
            <CodeBlock>{lastResponse.command}</CodeBlock>
          </Section>

          <Section title="Rendered environment">
            {Object.keys(lastResponse.env).length === 0 ? (
              <Typography variant="body2" color="text.secondary">
                (empty)
              </Typography>
            ) : (
              <Box
                sx={{
                  border: '1px solid',
                  borderColor: 'divider',
                  borderRadius: 1,
                  overflow: 'hidden',
                }}
              >
                <Table size="small">
                  <TableHead>
                    <TableRow>
                      <TableCell>Key</TableCell>
                      <TableCell>Value</TableCell>
                    </TableRow>
                  </TableHead>
                  <TableBody>
                    {Object.entries(lastResponse.env).map(([k, v]) => (
                      <TableRow key={k}>
                        <TableCell sx={MONO_SX}>{k}</TableCell>
                        <TableCell sx={MONO_SX}>{v}</TableCell>
                      </TableRow>
                    ))}
                  </TableBody>
                </Table>
              </Box>
            )}
          </Section>

          {lastResponse.healthcheckCommand && (
            <Section title="Rendered healthcheck">
              <CodeBlock>{lastResponse.healthcheckCommand}</CodeBlock>
            </Section>
          )}

          {lastResponse.installContribution && (
            <Section title="Install contribution">
              <CodeBlock>{lastResponse.installContribution}</CodeBlock>
            </Section>
          )}

          {lastResponse.setupContribution && (
            <Section title="Setup contribution">
              <CodeBlock>{lastResponse.setupContribution}</CodeBlock>
            </Section>
          )}

          {lastResponse.installVerify && (
            <Section title="Install verify">
              <CodeBlock>{lastResponse.installVerify}</CodeBlock>
            </Section>
          )}
        </>
      )}
    </Stack>
  )
}

function Section({
  title,
  children,
}: {
  title: string
  children: React.ReactNode
}) {
  return (
    <Box>
      <Typography
        variant="caption"
        sx={{
          textTransform: 'uppercase',
          letterSpacing: '0.08em',
          fontWeight: 600,
          color: 'text.secondary',
          mb: 0.75,
          display: 'block',
        }}
      >
        {title}
      </Typography>
      {children}
    </Box>
  )
}

function CodeBlock({ children }: { children: string }) {
  return (
    <Box
      component="pre"
      sx={{
        m: 0,
        p: 1.5,
        bgcolor: 'background.default',
        border: '1px solid',
        borderColor: 'divider',
        borderRadius: 1,
        fontFamily: 'monospace',
        fontSize: 12.5,
        lineHeight: 1.55,
        whiteSpace: 'pre-wrap',
        wordBreak: 'break-word',
        maxHeight: 320,
        overflowY: 'auto',
      }}
    >
      {children}
    </Box>
  )
}

function coerceValue(
  type: string,
  raw: string,
): string | number | boolean {
  if (type === PresetParameterType.Integer) {
    const n = Number(raw)
    if (!Number.isNaN(n)) return n
  }
  if (type === PresetParameterType.Boolean) {
    if (raw.toLowerCase() === 'true') return true
    if (raw.toLowerCase() === 'false') return false
  }
  return raw
}

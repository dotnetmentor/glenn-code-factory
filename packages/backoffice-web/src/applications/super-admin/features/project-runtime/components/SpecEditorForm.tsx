import { useCallback, useEffect, useRef, useState } from 'react'
import {
  Box,
  Button,
  IconButton,
  Stack,
  TextField,
  Tooltip,
  Typography,
} from '@mui/material'
import AddIcon from '@mui/icons-material/Add'
import DeleteOutlineIcon from '@mui/icons-material/DeleteOutline'
import type { RuntimeSpecV3, ServiceInstance } from '@/api/queries-commands'
import {
  workspaceFontFamily,
  workspaceRuntime,
  workspaceText,
} from '@/applications/workspace/shared/designTokens'
import type { SpecValidation } from './specValidation'
import { PresetPickerDialog } from './PresetPickerDialog'

const MONOSPACE_FONT = workspaceFontFamily.mono

export interface SpecEditorFormProps {
  spec: RuntimeSpecV3
  validation: SpecValidation
  onChange: (next: RuntimeSpecV3) => void
}

/**
 * Form view of the runtime spec editor (V3). Each service is now a
 * preset selection ({@code kind}) + a {@code name} + a freeform JSON
 * {@code values} object. The freeform per-service command / env /
 * healthcheck fields are gone — the backend expander materialises those
 * from the preset template at proposal time.
 *
 * <p>For visual / editing power-user parity with the previous form-mode
 * editor (which let operators edit env vars row-by-row), the values block
 * is rendered as a single monospace JSON textarea. Power users can switch
 * to the JSON tab at the dialog level for the full freeform spec.</p>
 */
export function SpecEditorForm({
  spec,
  validation,
  onChange,
}: SpecEditorFormProps) {
  const services = spec.services ?? []
  const [pickerOpen, setPickerOpen] = useState(false)
  const lastServiceRef = useRef<HTMLDivElement | null>(null)
  const pendingScrollRef = useRef(false)

  const updateService = useCallback(
    (index: number, mutate: (svc: ServiceInstance) => ServiceInstance) => {
      const next = services.slice()
      next[index] = mutate(next[index])
      onChange({ ...spec, services: next })
    },
    [services, spec, onChange],
  )

  const removeService = useCallback(
    (index: number) => {
      const next = services.slice()
      next.splice(index, 1)
      onChange({ ...spec, services: next })
    },
    [services, spec, onChange],
  )

  const appendServiceWithUniqueName = useCallback(
    (service: ServiceInstance) => {
      const existingNames = new Set(
        services.map((s: ServiceInstance) => s.name).filter((n: string) => n.length > 0),
      )
      let finalName = service.name
      if (finalName.length > 0 && existingNames.has(finalName)) {
        let counter = 2
        while (existingNames.has(`${service.name}-${counter}`)) {
          counter += 1
        }
        finalName = `${service.name}-${counter}`
      }
      const next: ServiceInstance[] = [
        ...services,
        { ...service, name: finalName },
      ]
      pendingScrollRef.current = true
      onChange({ ...spec, services: next })
    },
    [services, spec, onChange],
  )

  const openPicker = useCallback(() => setPickerOpen(true), [])
  const closePicker = useCallback(() => setPickerOpen(false), [])

  const handlePresetSelect = useCallback(
    (service: ServiceInstance) => {
      appendServiceWithUniqueName(service)
      setPickerOpen(false)
    },
    [appendServiceWithUniqueName],
  )

  useEffect(() => {
    if (!pendingScrollRef.current) return
    pendingScrollRef.current = false
    const id = requestAnimationFrame(() => {
      lastServiceRef.current?.scrollIntoView({
        behavior: 'smooth',
        block: 'start',
      })
    })
    return () => cancelAnimationFrame(id)
  }, [services.length])

  return (
    <Stack spacing={3} sx={{ p: { xs: 3, md: 4 }, maxWidth: 920, mx: 'auto', width: '100%' }}>
      <SectionHeader
        title="Install"
        subtitle="One-time setup. Re-runs only when the hash of this script changes."
      />
      <TextField
        label="Install"
        value={spec.install ?? ''}
        onChange={(e) =>
          onChange({ ...spec, install: e.target.value || null })
        }
        multiline
        minRows={4}
        maxRows={20}
        fullWidth
        placeholder="apt-get install -y mongodb-org"
        slotProps={{
          input: {
            sx: { fontFamily: MONOSPACE_FONT, fontSize: 13 },
          },
        }}
      />

      <SectionHeader
        title="Services"
        subtitle="Long-running processes managed by supervisord. Each service is a preset selection plus a values object the preset's template uses."
      />
      <Stack spacing={2}>
        {services.length === 0 && (
          <Typography
            sx={{
              fontSize: 13.5,
              color: workspaceText.muted,
              fontStyle: 'italic',
            }}
          >
            No services configured yet. Add one below.
          </Typography>
        )}
        {services.map((service: ServiceInstance, index: number) => (
          <ServiceCard
            key={index}
            index={index}
            service={service}
            nameError={validation.serviceErrors[index]?.name}
            kindError={validation.serviceErrors[index]?.kind}
            onChange={(mutate) => updateService(index, mutate)}
            onRemove={() => removeService(index)}
            cardRef={
              index === services.length - 1 ? lastServiceRef : undefined
            }
          />
        ))}
        <Box>
          <Button
            variant="quietOutlined"
            startIcon={<AddIcon sx={{ fontSize: 16 }} />}
            onClick={openPicker}
            data-testid="spec-editor-add-service"
          >
            Add service
          </Button>
        </Box>
      </Stack>

      <SectionHeader
        title="Setup"
        subtitle="Runs every boot after services are healthy (e.g. migrations). Presets contribute to this automatically."
      />
      <TextField
        label="Setup"
        value={spec.setup ?? ''}
        onChange={(e) => onChange({ ...spec, setup: e.target.value || null })}
        multiline
        minRows={4}
        maxRows={20}
        fullWidth
        placeholder="npm install && npm run migrate"
        slotProps={{
          input: {
            sx: { fontFamily: MONOSPACE_FONT, fontSize: 13 },
          },
        }}
      />

      <PresetPickerDialog
        open={pickerOpen}
        onClose={closePicker}
        onSelect={handlePresetSelect}
      />
    </Stack>
  )
}

function SectionHeader({
  title,
  subtitle,
}: {
  title: string
  subtitle?: string
}) {
  return (
    <Box>
      <Typography
        sx={{
          fontSize: '0.6875rem',
          fontWeight: 600,
          letterSpacing: '0.08em',
          textTransform: 'uppercase',
          color: workspaceText.muted,
        }}
      >
        {title}
      </Typography>
      {subtitle && (
        <Typography
          sx={{
            fontSize: 12.5,
            color: workspaceText.faint,
            letterSpacing: '-0.005em',
            mt: 0.25,
          }}
        >
          {subtitle}
        </Typography>
      )}
    </Box>
  )
}

interface ServiceCardProps {
  index: number
  service: ServiceInstance
  nameError?: string
  kindError?: string
  onChange: (mutate: (svc: ServiceInstance) => ServiceInstance) => void
  onRemove: () => void
  cardRef?: React.Ref<HTMLDivElement>
}

function ServiceCard({
  index,
  service,
  nameError,
  kindError,
  onChange,
  onRemove,
  cardRef,
}: ServiceCardProps) {
  const valuesString = useFormattedValues(service.values)
  const [valuesText, setValuesText] = useState<string>(valuesString)
  const [valuesError, setValuesError] = useState<string | null>(null)

  // Resync from external prop when the parent replaces the service (e.g.
  // user picked a different preset). Skipped when the user is mid-edit and
  // their text is already canonical-equivalent.
  useEffect(() => {
    setValuesText(valuesString)
    setValuesError(null)
  }, [valuesString])

  const handleValuesChange = useCallback(
    (raw: string) => {
      setValuesText(raw)
      const trimmed = raw.trim()
      if (trimmed.length === 0) {
        setValuesError(null)
        onChange((svc) => ({ ...svc, values: {} }))
        return
      }
      try {
        const parsed = JSON.parse(trimmed) as unknown
        if (
          !parsed ||
          typeof parsed !== 'object' ||
          Array.isArray(parsed)
        ) {
          setValuesError('Values must be a JSON object.')
          return
        }
        setValuesError(null)
        onChange((svc) => ({
          ...svc,
          values: parsed as Record<string, unknown>,
        }))
      } catch (err) {
        setValuesError(
          err instanceof Error ? err.message : 'Invalid JSON.',
        )
      }
    },
    [onChange],
  )

  return (
    <Box
      ref={cardRef}
      sx={{
        backgroundColor: 'instrument.canvas',
        border: 1,
        borderColor: 'instrument.hairline',
        borderRadius: 1.5,
        p: 2.5,
        transition: 'border-color 160ms ease',
        '&:hover': {
          borderColor: 'rgba(0,0,0,0.12)',
        },
      }}
    >
      <Stack spacing={2}>
        <Stack direction="row" alignItems="center" spacing={1}>
          <Stack direction="row" spacing={1.25} alignItems="baseline" sx={{ flex: 1, minWidth: 0 }}>
            <Typography
              sx={{
                fontSize: '0.6875rem',
                fontWeight: 600,
                letterSpacing: '0.08em',
                textTransform: 'uppercase',
                color: workspaceText.muted,
              }}
            >
              Service {index + 1}
            </Typography>
            {service.name && (
              <Typography
                sx={{
                  fontSize: 13,
                  fontFamily: MONOSPACE_FONT,
                  color: workspaceText.primary,
                  fontWeight: 500,
                }}
              >
                {service.name}
              </Typography>
            )}
          </Stack>
          <Tooltip title="Remove service" enterDelay={400}>
            <IconButton
              size="small"
              onClick={onRemove}
              aria-label={`Remove service ${index + 1}`}
              sx={{
                color: workspaceText.muted,
                '&:hover': {
                  color: workspaceRuntime.failed,
                  backgroundColor: 'rgba(178, 84, 56, 0.06)',
                },
              }}
            >
              <DeleteOutlineIcon sx={{ fontSize: 16 }} />
            </IconButton>
          </Tooltip>
        </Stack>

        <TextField
          label="Name"
          value={service.name}
          onChange={(e) =>
            onChange((svc) => ({ ...svc, name: e.target.value }))
          }
          required
          fullWidth
          error={!!nameError}
          helperText={nameError ?? 'Must be unique across services.'}
          size="small"
        />

        <TextField
          label="Kind (preset slug)"
          value={service.kind}
          onChange={(e) =>
            onChange((svc) => ({ ...svc, kind: e.target.value }))
          }
          required
          fullWidth
          placeholder="dotnet-mise, node-vite, postgres-15, …"
          error={!!kindError}
          helperText={kindError ?? 'Server validates that this preset exists.'}
          size="small"
          slotProps={{
            input: {
              sx: { fontFamily: MONOSPACE_FONT, fontSize: 13 },
            },
          }}
        />

        <TextField
          label="Values (JSON)"
          value={valuesText}
          onChange={(e) => handleValuesChange(e.target.value)}
          fullWidth
          multiline
          minRows={3}
          maxRows={12}
          placeholder='{ "project": "packages/api", "port": 5338 }'
          error={!!valuesError}
          helperText={
            valuesError ??
            'Object mapping each PresetParameter.key to a string / number / boolean.'
          }
          slotProps={{
            input: {
              sx: { fontFamily: MONOSPACE_FONT, fontSize: 13 },
            },
          }}
          size="small"
        />
      </Stack>
    </Box>
  )
}

/**
 * Memoised pretty-print of a service's {@code values} object. Stable
 * (sorted) key order so the textarea doesn't reshuffle as the user edits
 * unrelated fields, and `null`/`undefined` is rendered as the empty object.
 */
function useFormattedValues(values: ServiceInstance['values']): string {
  if (values == null) return '{}'
  try {
    return JSON.stringify(values, null, 2)
  } catch {
    return '{}'
  }
}

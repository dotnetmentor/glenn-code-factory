import {
  Autocomplete,
  Box,
  Checkbox,
  Chip,
  FormControlLabel,
  FormGroup,
  TextField,
  Typography,
} from '@mui/material'

/**
 * Reusable stack-picker form fields. Pulled out of {@link ManualStackPicker}
 * so the "Edit proposal" surface (Card 8) can render the same inputs without
 * duplicating the catalog or the parsing of the free-text extras field.
 *
 * Controlled component: parent owns the {@link StackPickerValue} state. On
 * submit, the parent reads `value.extras` directly — the splitting on
 * whitespace/comma already happened on each keystroke via {@link parseExtras}.
 */

export type StackPickerValue = {
  /** Language identifiers, e.g. "node@22", "python@3.12". */
  languages: string[]
  /** Service identifiers, e.g. "postgres", "redis". */
  services: string[]
  /** Free-text extras (e.g. "pnpm", "ffmpeg"). */
  extras: string[]
}

export const LANGUAGE_OPTIONS = [
  { value: 'node@22', label: 'Node.js 22' },
  { value: 'python@3.12', label: 'Python 3.12' },
  { value: 'go@1.23', label: 'Go 1.23' },
  { value: 'rust', label: 'Rust' },
] as const

export const SERVICE_OPTIONS = [
  { value: 'postgres', label: 'PostgreSQL' },
  { value: 'redis', label: 'Redis' },
  { value: 'minio', label: 'MinIO (S3-compatible)' },
  { value: 'mailhog', label: 'MailHog' },
] as const

/** Split a comma- or whitespace-separated string into trimmed, non-empty tokens. */
export function parseExtras(raw: string): string[] {
  return raw
    .split(/[,\s]+/)
    .map((t) => t.trim())
    .filter((t) => t.length > 0)
}

interface StackPickerFieldsProps {
  value: StackPickerValue
  onChange: (next: StackPickerValue) => void
  /** Raw text in the "Anything else?" textbox; parent owns it so the user can
   * type spaces without us re-splitting on every keystroke. */
  extrasRaw: string
  onExtrasRawChange: (raw: string) => void
  disabled?: boolean
}

export function StackPickerFields({
  value,
  onChange,
  extrasRaw,
  onExtrasRawChange,
  disabled,
}: StackPickerFieldsProps) {
  const langOptions = LANGUAGE_OPTIONS.map((o) => o.value)

  return (
    <Box sx={{ display: 'flex', flexDirection: 'column', gap: 3 }}>
      <Box>
        <Typography variant="subtitle2" sx={{ mb: 1 }}>
          Languages
        </Typography>
        <Autocomplete
          multiple
          disabled={disabled}
          options={langOptions}
          value={value.languages}
          onChange={(_e, next) => onChange({ ...value, languages: next })}
          getOptionLabel={(opt) =>
            LANGUAGE_OPTIONS.find((o) => o.value === opt)?.label ?? opt
          }
          renderTags={(selected, getTagProps) =>
            selected.map((opt, index) => {
              const { key, ...tagProps } = getTagProps({ index })
              return (
                <Chip
                  key={key}
                  variant="filled"
                  label={LANGUAGE_OPTIONS.find((o) => o.value === opt)?.label ?? opt}
                  {...tagProps}
                />
              )
            })
          }
          renderInput={(params) => (
            <TextField
              {...params}
              placeholder={value.languages.length === 0 ? 'Pick one or more' : ''}
              helperText="Versions are resolved by mise inside the runtime."
            />
          )}
        />
      </Box>

      <Box>
        <Typography variant="subtitle2" sx={{ mb: 1 }}>
          Services
        </Typography>
        <FormGroup>
          {SERVICE_OPTIONS.map((svc) => {
            const checked = value.services.includes(svc.value)
            return (
              <FormControlLabel
                key={svc.value}
                control={
                  <Checkbox
                    checked={checked}
                    disabled={disabled}
                    onChange={(_e, isChecked) => {
                      const nextServices = isChecked
                        ? [...value.services, svc.value]
                        : value.services.filter((s) => s !== svc.value)
                      onChange({ ...value, services: nextServices })
                    }}
                  />
                }
                label={svc.label}
              />
            )
          })}
        </FormGroup>
      </Box>

      <Box>
        <Typography variant="subtitle2" sx={{ mb: 1 }}>
          Anything else?
        </Typography>
        <TextField
          fullWidth
          disabled={disabled}
          value={extrasRaw}
          onChange={(e) => {
            const raw = e.target.value
            onExtrasRawChange(raw)
            onChange({ ...value, extras: parseExtras(raw) })
          }}
          placeholder="pnpm, ffmpeg"
          helperText="Comma- or space-separated (e.g. pnpm, ffmpeg)"
        />
      </Box>
    </Box>
  )
}

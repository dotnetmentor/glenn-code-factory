import { useState } from 'react'
import {
  Accordion,
  AccordionDetails,
  AccordionSummary,
  Box,
  Button,
  Chip,
  CircularProgress,
  FormControl,
  FormControlLabel,
  IconButton,
  InputLabel,
  MenuItem,
  Popover,
  Select,
  Stack,
  Switch,
  TextField,
  Tooltip,
  Typography,
} from '@mui/material'
import AddIcon from '@mui/icons-material/Add'
import DeleteOutlineIcon from '@mui/icons-material/DeleteOutline'
import ExpandMoreIcon from '@mui/icons-material/ExpandMore'
import SearchIcon from '@mui/icons-material/Search'
import {
  PresetParameterType,
  type PresetParameter,
} from '@/api/queries-commands'
import { useMiseVersions } from '../hooks/useMiseVersions'

export interface ParameterEditorProps {
  value: PresetParameter[]
  onChange: (next: PresetParameter[]) => void
  readOnly?: boolean
}

function emptyParameter(): PresetParameter {
  return {
    key: '',
    label: '',
    type: PresetParameterType.String,
    required: false,
    defaultValue: '',
    enumOptions: null,
    description: '',
    miseTool: '',
    helpUrl: '',
  }
}

/**
 * Add/edit/remove the {@link PresetParameter[]} stored on a
 * {@code ServicePreset}. Each parameter is rendered as a collapsible
 * Accordion row so the editor stays compact even for presets with 10+
 * parameters.
 *
 * <p>For {@code Type=Enum} the editor surfaces a comma-separated
 * EnumOptions input. For {@code MiseTool} non-empty, a "Lookup versions"
 * button fires the {@code mise-versions} endpoint and renders the result
 * as clickable chips that copy to the default value.</p>
 */
export function ParameterEditor({
  value,
  onChange,
  readOnly,
}: ParameterEditorProps) {
  const updateAt = (index: number, mutate: (p: PresetParameter) => PresetParameter) => {
    const next = value.slice()
    next[index] = mutate(next[index])
    onChange(next)
  }

  const removeAt = (index: number) => {
    const next = value.slice()
    next.splice(index, 1)
    onChange(next)
  }

  const add = () => {
    onChange([...value, emptyParameter()])
  }

  return (
    <Stack spacing={1.5}>
      {value.length === 0 && (
        <Typography variant="body2" color="text.secondary">
          No parameters yet. Add one to expose a knob the agent can fill in.
        </Typography>
      )}

      {value.map((param, index) => (
        <ParameterRow
          key={index}
          index={index}
          parameter={param}
          onChange={(mutate) => updateAt(index, mutate)}
          onRemove={() => removeAt(index)}
          readOnly={readOnly}
        />
      ))}

      {!readOnly && (
        <Box>
          <Button
            variant="outlined"
            size="small"
            startIcon={<AddIcon />}
            onClick={add}
            sx={{ textTransform: 'none' }}
          >
            Add parameter
          </Button>
        </Box>
      )}
    </Stack>
  )
}

interface ParameterRowProps {
  index: number
  parameter: PresetParameter
  onChange: (mutate: (p: PresetParameter) => PresetParameter) => void
  onRemove: () => void
  readOnly?: boolean
}

function ParameterRow({
  index,
  parameter,
  onChange,
  onRemove,
  readOnly,
}: ParameterRowProps) {
  const headerTitle =
    parameter.label || parameter.key || `Parameter ${index + 1}`

  return (
    <Accordion
      disableGutters
      sx={{
        '&:before': { display: 'none' },
        border: '1px solid',
        borderColor: 'divider',
        borderRadius: 1.5,
        overflow: 'hidden',
      }}
    >
      <AccordionSummary expandIcon={<ExpandMoreIcon />}>
        <Stack
          direction="row"
          spacing={1.5}
          sx={{ alignItems: 'center', width: '100%' }}
        >
          <Typography sx={{ fontWeight: 500, flex: 1, minWidth: 0 }} noWrap>
            {headerTitle}
          </Typography>
          {parameter.key && (
            <Typography
              component="span"
              sx={{
                fontFamily: 'monospace',
                fontSize: '0.75rem',
                color: 'text.secondary',
              }}
            >
              {parameter.key}
            </Typography>
          )}
          <Chip size="small" label={parameter.type} variant="outlined" />
          {parameter.required && (
            <Chip size="small" label="Required" color="primary" variant="outlined" />
          )}
        </Stack>
      </AccordionSummary>
      <AccordionDetails>
        <Stack spacing={2}>
          <Stack direction={{ xs: 'column', sm: 'row' }} spacing={2}>
            <TextField
              label="Key"
              value={parameter.key}
              onChange={(e) => onChange((p) => ({ ...p, key: e.target.value }))}
              size="small"
              fullWidth
              disabled={readOnly}
              required
              slotProps={{
                input: { sx: { fontFamily: 'monospace', fontSize: 13 } },
              }}
            />
            <TextField
              label="Label"
              value={parameter.label}
              onChange={(e) =>
                onChange((p) => ({ ...p, label: e.target.value }))
              }
              size="small"
              fullWidth
              disabled={readOnly}
            />
          </Stack>

          <Stack direction={{ xs: 'column', sm: 'row' }} spacing={2}>
            <FormControl size="small" fullWidth>
              <InputLabel>Type</InputLabel>
              <Select
                label="Type"
                value={parameter.type}
                disabled={readOnly}
                onChange={(e) =>
                  onChange((p) => ({
                    ...p,
                    type: e.target.value as PresetParameter['type'],
                  }))
                }
              >
                {Object.values(PresetParameterType).map((t) => (
                  <MenuItem key={t} value={t}>
                    {t}
                  </MenuItem>
                ))}
              </Select>
            </FormControl>

            <FormControlLabel
              control={
                <Switch
                  checked={parameter.required}
                  disabled={readOnly}
                  onChange={(e) =>
                    onChange((p) => ({ ...p, required: e.target.checked }))
                  }
                />
              }
              label="Required"
            />
          </Stack>

          <DefaultValueField
            parameter={parameter}
            onChange={onChange}
            readOnly={readOnly}
          />

          {parameter.type === PresetParameterType.Enum && (
            <EnumOptionsField
              parameter={parameter}
              onChange={onChange}
              readOnly={readOnly}
            />
          )}

          <TextField
            label="Description"
            value={parameter.description ?? ''}
            onChange={(e) =>
              onChange((p) => ({ ...p, description: e.target.value || null }))
            }
            size="small"
            fullWidth
            multiline
            minRows={2}
            disabled={readOnly}
          />

          <Stack direction={{ xs: 'column', sm: 'row' }} spacing={2}>
            <MiseToolField
              parameter={parameter}
              onChange={onChange}
              readOnly={readOnly}
            />
            <TextField
              label="Help URL"
              value={parameter.helpUrl ?? ''}
              onChange={(e) =>
                onChange((p) => ({ ...p, helpUrl: e.target.value || null }))
              }
              size="small"
              fullWidth
              disabled={readOnly}
            />
          </Stack>

          {!readOnly && (
            <Box sx={{ display: 'flex', justifyContent: 'flex-end' }}>
              <Tooltip title="Remove parameter">
                <IconButton size="small" color="error" onClick={onRemove}>
                  <DeleteOutlineIcon fontSize="small" />
                </IconButton>
              </Tooltip>
            </Box>
          )}
        </Stack>
      </AccordionDetails>
    </Accordion>
  )
}

function DefaultValueField({
  parameter,
  onChange,
  readOnly,
}: {
  parameter: PresetParameter
  onChange: (mutate: (p: PresetParameter) => PresetParameter) => void
  readOnly?: boolean
}) {
  if (parameter.type === PresetParameterType.Boolean) {
    const checked = (parameter.defaultValue ?? '').toLowerCase() === 'true'
    return (
      <FormControlLabel
        control={
          <Switch
            checked={checked}
            disabled={readOnly}
            onChange={(e) =>
              onChange((p) => ({
                ...p,
                defaultValue: e.target.checked ? 'true' : 'false',
              }))
            }
          />
        }
        label="Default value (true / false)"
      />
    )
  }

  if (parameter.type === PresetParameterType.Enum) {
    const options = parameter.enumOptions ?? []
    return (
      <FormControl size="small" fullWidth>
        <InputLabel>Default value</InputLabel>
        <Select
          label="Default value"
          value={parameter.defaultValue ?? ''}
          disabled={readOnly}
          onChange={(e) =>
            onChange((p) => ({
              ...p,
              defaultValue: (e.target.value as string) || null,
            }))
          }
          displayEmpty
        >
          <MenuItem value="">
            <em>None</em>
          </MenuItem>
          {options.map((opt) => (
            <MenuItem key={opt} value={opt}>
              {opt}
            </MenuItem>
          ))}
        </Select>
      </FormControl>
    )
  }

  return (
    <TextField
      label="Default value"
      value={parameter.defaultValue ?? ''}
      onChange={(e) =>
        onChange((p) => ({ ...p, defaultValue: e.target.value || null }))
      }
      size="small"
      fullWidth
      type={parameter.type === PresetParameterType.Integer ? 'number' : 'text'}
      disabled={readOnly}
      slotProps={{
        input: { sx: { fontFamily: 'monospace', fontSize: 13 } },
      }}
    />
  )
}

function EnumOptionsField({
  parameter,
  onChange,
  readOnly,
}: {
  parameter: PresetParameter
  onChange: (mutate: (p: PresetParameter) => PresetParameter) => void
  readOnly?: boolean
}) {
  const raw = (parameter.enumOptions ?? []).join(', ')
  return (
    <TextField
      label="Enum options (comma-separated)"
      value={raw}
      onChange={(e) => {
        const parsed = e.target.value
          .split(',')
          .map((s) => s.trim())
          .filter((s) => s.length > 0)
        onChange((p) => ({
          ...p,
          enumOptions: parsed.length === 0 ? null : parsed,
        }))
      }}
      size="small"
      fullWidth
      placeholder="e.g. 7, 8, 9"
      disabled={readOnly}
      slotProps={{
        input: { sx: { fontFamily: 'monospace', fontSize: 13 } },
      }}
    />
  )
}

function MiseToolField({
  parameter,
  onChange,
  readOnly,
}: {
  parameter: PresetParameter
  onChange: (mutate: (p: PresetParameter) => PresetParameter) => void
  readOnly?: boolean
}) {
  const tool = parameter.miseTool ?? ''
  const [anchorEl, setAnchorEl] = useState<HTMLElement | null>(null)
  const { versions, isLoading, isError } = useMiseVersions(
    anchorEl ? tool : undefined,
  )

  const open = !!anchorEl

  return (
    <Stack direction="row" spacing={1} sx={{ flex: 1, alignItems: 'flex-start' }}>
      <TextField
        label="Mise tool"
        value={tool}
        onChange={(e) =>
          onChange((p) => ({ ...p, miseTool: e.target.value || null }))
        }
        size="small"
        fullWidth
        placeholder="dotnet, node, python, …"
        disabled={readOnly}
        slotProps={{
          input: { sx: { fontFamily: 'monospace', fontSize: 13 } },
        }}
        helperText="Optional. If set, the editor offers a version lookup."
      />
      <Tooltip
        title={
          tool ? 'Lookup mise versions for this tool' : 'Enter a tool first'
        }
      >
        <span>
          <IconButton
            size="small"
            onClick={(e) => setAnchorEl(e.currentTarget)}
            disabled={!tool}
            sx={{ mt: 0.5 }}
          >
            <SearchIcon fontSize="small" />
          </IconButton>
        </span>
      </Tooltip>
      <Popover
        open={open}
        anchorEl={anchorEl}
        onClose={() => setAnchorEl(null)}
        anchorOrigin={{ vertical: 'bottom', horizontal: 'right' }}
        transformOrigin={{ vertical: 'top', horizontal: 'right' }}
        slotProps={{ paper: { sx: { p: 2, maxWidth: 360 } } }}
      >
        <Stack spacing={1}>
          <Typography variant="subtitle2">{`mise versions: ${tool}`}</Typography>
          {isLoading && (
            <Stack direction="row" spacing={1} alignItems="center">
              <CircularProgress size={14} />
              <Typography variant="body2" color="text.secondary">
                Looking up…
              </Typography>
            </Stack>
          )}
          {isError && (
            <Typography variant="body2" color="error">
              Failed to load versions.
            </Typography>
          )}
          {!isLoading && !isError && versions.length === 0 && (
            <Typography variant="body2" color="text.secondary">
              No versions returned.
            </Typography>
          )}
          {versions.length > 0 && (
            <Stack
              direction="row"
              spacing={0.75}
              flexWrap="wrap"
              useFlexGap
              sx={{ rowGap: 0.75 }}
            >
              {versions.map((v) => (
                <Chip
                  key={v}
                  label={v}
                  size="small"
                  variant="outlined"
                  clickable
                  onClick={() => {
                    onChange((p) => ({ ...p, defaultValue: v }))
                    setAnchorEl(null)
                  }}
                />
              ))}
            </Stack>
          )}
        </Stack>
      </Popover>
    </Stack>
  )
}

import { Box, Button, IconButton, Stack, TextField, Typography } from '@mui/material'
import AddIcon from '@mui/icons-material/Add'
import DeleteOutlineIcon from '@mui/icons-material/DeleteOutline'

export interface StringListEditorProps {
  /** Human-readable label rendered above the list (e.g. "Allowed Tools"). */
  label: string
  /** Optional caption/help text under the label. */
  helperText?: string
  /** Current list value. */
  value: string[]
  /** Called with the next list when any row changes or is added/removed. */
  onChange: (next: string[]) => void
  /** Placeholder for each input row. */
  placeholder?: string
  /** Label for the "Add" button. Defaults to "Add row". */
  addLabel?: string
  /** Disable editing entirely (read-only mode). */
  disabled?: boolean
}

/**
 * Tiny list editor — one {@code TextField} per row, plus a remove button per
 * row and an {@code Add row} button at the bottom. Used by the project's
 * Agent Permissions settings page to edit AllowedTools / DisallowedTools /
 * AdditionalDirectories. Intentionally light: no drag-to-reorder, no
 * deduping. The 5-field permissions surface doesn't need it in v1.
 */
export function StringListEditor({
  label,
  helperText,
  value,
  onChange,
  placeholder,
  addLabel = 'Add row',
  disabled = false,
}: StringListEditorProps) {
  const handleRowChange = (index: number, next: string) => {
    const copy = value.slice()
    copy[index] = next
    onChange(copy)
  }
  const handleRemove = (index: number) => {
    const copy = value.slice()
    copy.splice(index, 1)
    onChange(copy)
  }
  const handleAdd = () => {
    onChange([...value, ''])
  }

  return (
    <Stack spacing={1}>
      <Box>
        <Typography variant="subtitle2" sx={{ fontWeight: 600 }}>
          {label}
        </Typography>
        {helperText && (
          <Typography variant="caption" color="text.secondary" component="div">
            {helperText}
          </Typography>
        )}
      </Box>

      {value.length === 0 && (
        <Typography
          variant="body2"
          color="text.secondary"
          sx={{ fontStyle: 'italic' }}
        >
          {disabled ? '(empty)' : 'No entries yet — click Add row below.'}
        </Typography>
      )}

      <Stack spacing={1}>
        {value.map((row, index) => (
          <Box
            key={index}
            sx={{ display: 'flex', alignItems: 'center', gap: 1 }}
          >
            <TextField
              value={row}
              onChange={(e) => handleRowChange(index, e.target.value)}
              placeholder={placeholder}
              size="small"
              fullWidth
              disabled={disabled}
              inputProps={{ 'aria-label': `${label} row ${index + 1}` }}
            />
            {!disabled && (
              <IconButton
                size="small"
                aria-label={`Remove ${label} row ${index + 1}`}
                onClick={() => handleRemove(index)}
              >
                <DeleteOutlineIcon fontSize="small" />
              </IconButton>
            )}
          </Box>
        ))}
      </Stack>

      {!disabled && (
        <Box>
          <Button
            size="small"
            startIcon={<AddIcon />}
            onClick={handleAdd}
            variant="text"
          >
            {addLabel}
          </Button>
        </Box>
      )}
    </Stack>
  )
}

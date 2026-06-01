import {
  Box,
  FormControl,
  InputLabel,
  MenuItem,
  Select,
  ToggleButton,
  ToggleButtonGroup,
  Typography,
} from '@mui/material'
import {
  WAKE_TIME_WINDOWS,
  type WakeTimeWindow,
} from '../hooks/useWakeObservability'

interface FilterRowProps {
  timeWindow: WakeTimeWindow
  onTimeWindowChange: (next: WakeTimeWindow) => void
  region: string | undefined
  onRegionChange: (next: string | undefined) => void
  /**
   * Available region codes — derived from the slow-sessions response so the
   * dropdown only ever shows regions that have actually been seen in the
   * fleet. Fleet-wide is represented as `undefined` via the "All regions"
   * sentinel option (see {@code ALL_REGIONS_VALUE}).
   */
  regionOptions: string[]
}

const TIME_WINDOW_LABELS: Record<WakeTimeWindow, string> = {
  '1h': 'Last 1h',
  '24h': 'Last 24h',
  '7d': 'Last 7d',
}

// Sentinel used by the Select to mean "fleet-wide" — MUI's Select can't take
// undefined as a value, so we round-trip an empty string and translate at the
// edges.
const ALL_REGIONS_VALUE = ''

export function FilterRow({
  timeWindow,
  onTimeWindowChange,
  region,
  onRegionChange,
  regionOptions,
}: FilterRowProps) {
  return (
    <Box
      sx={{
        display: 'flex',
        alignItems: 'center',
        gap: 2,
        flexWrap: 'wrap',
      }}
    >
      <FormControl size="small" sx={{ minWidth: 180 }}>
        <InputLabel id="wake-region-label">Region</InputLabel>
        <Select
          labelId="wake-region-label"
          label="Region"
          value={region ?? ALL_REGIONS_VALUE}
          onChange={(e) => {
            const next = e.target.value
            onRegionChange(next === ALL_REGIONS_VALUE ? undefined : next)
          }}
        >
          <MenuItem value={ALL_REGIONS_VALUE}>
            <em>All regions (fleet-wide)</em>
          </MenuItem>
          {regionOptions.map((r) => (
            <MenuItem key={r} value={r}>
              {r}
            </MenuItem>
          ))}
          {/*
           * Edge case: user has a region in the URL that isn't in the
           * derived option list (because that region had no slow sessions
           * in the current window). Show it anyway so the Select renders
           * the value the URL asked for instead of dropping silently.
           */}
          {region && !regionOptions.includes(region) && (
            <MenuItem key={region} value={region}>
              {region}
            </MenuItem>
          )}
        </Select>
      </FormControl>

      <Box>
        <Typography
          variant="caption"
          color="text.secondary"
          sx={{ display: 'block', mb: 0.5 }}
        >
          Time window
        </Typography>
        <ToggleButtonGroup
          size="small"
          exclusive
          value={timeWindow}
          onChange={(_e, value: WakeTimeWindow | null) => {
            if (value) onTimeWindowChange(value)
          }}
          aria-label="Time window"
        >
          {WAKE_TIME_WINDOWS.map((w) => (
            <ToggleButton key={w} value={w} aria-label={TIME_WINDOW_LABELS[w]}>
              {w}
            </ToggleButton>
          ))}
        </ToggleButtonGroup>
      </Box>
    </Box>
  )
}

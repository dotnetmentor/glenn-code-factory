import { Box, Tab, Tabs } from '@mui/material'
import { useState } from 'react'
import type {
  SystemSettingCategoryDto,
  SystemSettingDto,
} from '../../../../../api/queries-commands'
import { CategoryPanel } from './CategoryPanel'

export interface SystemSettingsTabsProps {
  categories: SystemSettingCategoryDto[]
  settings: SystemSettingDto[]
}

export function SystemSettingsTabs({
  categories,
  settings,
}: SystemSettingsTabsProps) {
  const [activeIndex, setActiveIndex] = useState(0)

  if (categories.length === 0) {
    return null
  }

  // Clamp to a valid index in case the list shrinks (very unlikely
  // in practice but cheap insurance).
  const safeIndex = Math.min(activeIndex, categories.length - 1)
  const active = categories[safeIndex]

  return (
    <Box>
      <Tabs
        value={safeIndex}
        onChange={(_, v) => setActiveIndex(v)}
        aria-label="System settings categories"
        sx={{ borderBottom: 1, borderColor: 'divider', mb: 3 }}
      >
        {categories.map((category, idx) => (
          <Tab
            key={category.key}
            label={category.displayName}
            id={`system-settings-tab-${idx}`}
            aria-controls={`system-settings-panel-${idx}`}
          />
        ))}
      </Tabs>

      <Box
        role="tabpanel"
        id={`system-settings-panel-${safeIndex}`}
        aria-labelledby={`system-settings-tab-${safeIndex}`}
      >
        {/* Render only the active panel — keeps inactive trees unmounted. */}
        <CategoryPanel category={active} settings={settings} />
      </Box>
    </Box>
  )
}

import { Stack } from '@mui/material'
import { useQueryClient } from '@tanstack/react-query'
import { useState } from 'react'
import {
  getGetApiSystemSettingsQueryKey,
  usePutApiSystemSettingsKey,
  type SystemSettingCategoryDto,
  type SystemSettingDto,
} from '../../../../../api/queries-commands'
import { useNotification } from '../../../../shared/contexts/NotificationContext'
import { FlyTestPanel } from './FlyTestPanel'
import { GitHubHelpPanel } from './GitHubHelpPanel'
import { GithubTestPanel } from './GithubTestPanel'
import { SettingField } from './SettingField'

export interface CategoryPanelProps {
  category: SystemSettingCategoryDto
  /** All settings from the list query, used to look up state by key. */
  settings: SystemSettingDto[]
}

export function CategoryPanel({ category, settings }: CategoryPanelProps) {
  const queryClient = useQueryClient()
  const { showSuccess, showError } = useNotification()
  const updateMutation = usePutApiSystemSettingsKey()
  const [savingKey, setSavingKey] = useState<string | null>(null)

  // Map for O(1) lookup by key.
  const stateByKey = new Map<string, SystemSettingDto>()
  for (const setting of settings) {
    stateByKey.set(setting.key, setting)
  }

  const handleSave = async (key: string, value: string) => {
    setSavingKey(key)
    try {
      await updateMutation.mutateAsync({ key, data: { value } })
      await queryClient.invalidateQueries({
        queryKey: getGetApiSystemSettingsQueryKey(),
      })
      const def = category.settings.find((s) => s.key === key)
      showSuccess(`${def?.displayName ?? key} saved.`)
    } catch (err) {
      const message = err instanceof Error ? err.message : 'Unknown error'
      const def = category.settings.find((s) => s.key === key)
      showError(`Failed to save ${def?.displayName ?? key}: ${message}`)
    } finally {
      setSavingKey(null)
    }
  }

  const isGitHub = category.key === 'GitHub'
  const isFly = category.key === 'Fly'

  return (
    <Stack spacing={3}>
      {isGitHub && <GitHubHelpPanel />}
      {isGitHub && <GithubTestPanel />}
      {isFly && <FlyTestPanel />}
      {category.settings.map((definition) => (
        <SettingField
          key={definition.key}
          definition={definition}
          state={stateByKey.get(definition.key)}
          isSaving={savingKey === definition.key}
          onSave={handleSave}
        />
      ))}
    </Stack>
  )
}

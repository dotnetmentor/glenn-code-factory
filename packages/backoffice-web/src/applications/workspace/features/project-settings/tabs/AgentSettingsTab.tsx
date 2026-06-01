import { useEffect, useMemo, useState } from 'react'
import {
  Box,
  CircularProgress,
  MenuItem,
  Select,
  Stack,
  Typography,
} from '@mui/material'
import { useQueryClient } from '@tanstack/react-query'
import {
  getGetApiProjectsProjectIdQueryKey,
  useGetApiCursorModelsActive,
  useGetApiProjectsProjectId,
  usePatchApiProjectsProjectIdCursorModel,
} from '../../../../../api/queries-commands'
import { useNotification } from '../../../../shared/contexts/NotificationContext'
import {
  bodySx,
  captionSx,
  sectionTitleSx,
  workspaceFontFamily,
  workspaceText,
} from '../../../shared'

// Sentinel value the MUI Select binds to when "Use system default" is picked.
const NULL_VALUE = '__null__'

interface AgentSettingsTabProps {
  projectId: string
}

/**
 * Agent tab — lets the user pick the default Cursor model new chat turns on
 * this project use. Per-conversation overrides in the chat chrome take
 * precedence over the project default when set.
 */
export function AgentSettingsTab({ projectId }: AgentSettingsTabProps) {
  const queryClient = useQueryClient()
  const { showSuccess, showError } = useNotification()

  const projectQuery = useGetApiProjectsProjectId(projectId, {
    query: { enabled: !!projectId },
  })
  const cursorModelsQuery = useGetApiCursorModelsActive({
    query: { staleTime: 30_000 },
  })

  const savedModelId = projectQuery.data?.modelId ?? null
  const savedModelSlug = projectQuery.data?.modelSlug ?? null

  const [modelDraft, setModelDraft] = useState<string>(savedModelId ?? NULL_VALUE)

  useEffect(() => {
    setModelDraft(savedModelId ?? NULL_VALUE)
  }, [savedModelId])

  const cursorModelMutation = usePatchApiProjectsProjectIdCursorModel()

  const cursorModels = cursorModelsQuery.data ?? []

  const savedModel = useMemo(
    () => cursorModels.find((m) => m.id === savedModelId) ?? null,
    [cursorModels, savedModelId],
  )

  const invalidateProject = () => {
    queryClient.invalidateQueries({
      queryKey: getGetApiProjectsProjectIdQueryKey(projectId),
    })
  }

  const handleModelChange = (next: string) => {
    setModelDraft(next)
    const nextId: string | null = next === NULL_VALUE ? null : next
    if (nextId === savedModelId) return
    cursorModelMutation.mutate(
      {
        projectId,
        data: { modelId: nextId },
      },
      {
        onSuccess: () => {
          const newName =
            nextId === null
              ? 'system default'
              : (cursorModels.find((m) => m.id === nextId)?.displayName ??
                'model')
          showSuccess(`Cursor model updated: ${newName}.`)
          invalidateProject()
        },
        onError: (error: unknown) => {
          const detail = (error as {
            response?: { data?: { detail?: string; error?: string } }
          })?.response?.data
          const code = detail?.detail ?? detail?.error
          if (code === 'invalid_cursor_model') {
            showError('The selected Cursor model is no longer available.')
          } else {
            showError('Could not update the Cursor model.')
          }
          setModelDraft(savedModelId ?? NULL_VALUE)
        },
      },
    )
  }

  const currentModelLabel = (() => {
    if (savedModel) {
      return `${savedModel.displayName} (${savedModel.slug})`
    }
    if (savedModelSlug) return savedModelSlug
    return 'system default'
  })()

  const cursorListEmpty =
    !cursorModelsQuery.isLoading && cursorModels.length === 0

  return (
    <Stack spacing={4}>
      <Box>
        <Typography
          component="h3"
          sx={{
            fontSize: '1.25rem',
            fontWeight: 400,
            letterSpacing: '-0.01em',
            color: workspaceText.primary,
            mb: 0.5,
          }}
        >
          Agent
        </Typography>
        <Typography sx={bodySx}>
          The default Cursor model new chat turns on this project use.
          Per-conversation overrides in the chat chrome win when set;
          otherwise new turns pick this value, falling back to the SDK default
          when empty.
        </Typography>
      </Box>

      <Box
        sx={{
          border: 1,
          borderColor: 'instrument.hairline',
          borderRadius: 2,
          p: { xs: 2.5, md: 3 },
        }}
      >
        <Stack spacing={3}>
          <Box>
            <Typography sx={sectionTitleSx}>Default model</Typography>
            <Typography sx={{ ...captionSx, mt: 0.5, mb: 1.5 }}>
              Pick which Cursor-routed model should handle new turns on this
              project.
            </Typography>
            {cursorModelsQuery.isLoading ? (
              <Box sx={{ display: 'flex', alignItems: 'center', gap: 1 }}>
                <CircularProgress size={16} />
                <Typography sx={captionSx}>Loading models…</Typography>
              </Box>
            ) : (
              <>
                <Select
                  value={modelDraft}
                  onChange={(e) => handleModelChange(e.target.value)}
                  size="small"
                  disabled={
                    cursorModelMutation.isPending ||
                    cursorModelsQuery.isError ||
                    cursorListEmpty
                  }
                  sx={{
                    minWidth: 280,
                    backgroundColor: 'instrument.inputBg',
                    fontFamily: workspaceFontFamily.sans,
                    fontSize: '0.875rem',
                    color: workspaceText.primary,
                  }}
                >
                  <MenuItem value={NULL_VALUE}>
                    <Box>
                      <Typography variant="body2" sx={{ fontWeight: 500 }}>
                        Use system default
                      </Typography>
                      <Typography variant="caption" color="text.secondary">
                        Defer to whatever the Cursor SDK picks (auto).
                      </Typography>
                    </Box>
                  </MenuItem>
                  {cursorModels.map((m) => (
                    <MenuItem key={m.id} value={m.id}>
                      <Box>
                        <Typography variant="body2" sx={{ fontWeight: 500 }}>
                          {m.displayName}
                        </Typography>
                        <Typography
                          variant="caption"
                          sx={{
                            color: 'text.secondary',
                            fontFamily:
                              '"SF Mono", "Fira Code", "Consolas", monospace',
                          }}
                        >
                          {m.slug}
                        </Typography>
                      </Box>
                    </MenuItem>
                  ))}
                </Select>
                {cursorListEmpty && (
                  <Typography sx={{ ...captionSx, mt: 1 }}>
                    No Cursor models available.
                  </Typography>
                )}
              </>
            )}
          </Box>

          <Stack
            direction="row"
            justifyContent="space-between"
            alignItems="center"
            sx={{ pt: 1 }}
          >
            <Typography sx={captionSx}>
              Current: {currentModelLabel}
            </Typography>
            <Typography sx={captionSx}>
              {cursorModelMutation.isPending ? 'Saving…' : ''}
            </Typography>
          </Stack>
        </Stack>
      </Box>
    </Stack>
  )
}

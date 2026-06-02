import { useMemo, useState } from 'react'
import {
  Alert,
  Box,
  Button,
  Skeleton,
  Stack,
  Typography,
} from '@mui/material'
import AddIcon from '@mui/icons-material/Add'
import ContentPasteIcon from '@mui/icons-material/ContentPaste'
import CheckCircleOutlineIcon from '@mui/icons-material/CheckCircleOutline'
import ErrorOutlineIcon from '@mui/icons-material/ErrorOutline'
import {
  type RequiredEnvStatusItem,
  type SuggestedEnvStatusItem,
} from '../../../../../../api/queries-commands'
import { useNotification } from '../../../../../shared/contexts/NotificationContext'
import {
  bodySx,
  semanticTokens,
  surfaceTokens,
  workspaceFontFamily,
  workspaceText,
} from '../../../../shared/designTokens'
import { useBranchEnvVars } from './useBranchEnvVars'
import { useProjectEnvVars } from './useProjectEnvVars'
import { EnvVarRow } from './EnvVarRow'
import { EnvVarDialog, type EnvVarDialogValues } from './EnvVarDialog'
import { DeleteEnvVarDialog } from './DeleteEnvVarDialog'
import { PasteEnvDialog } from './PasteEnvDialog'
import { parseDotEnv } from './parseDotEnv'
import type { EnvVarListItem, EnvVarScope } from './envVarTypes'

interface EnvironmentTabProps {
  projectId: string
  /** When set, shows branch runtime status and optional per-branch overrides. */
  branchId?: string
}

interface DialogState {
  mode: 'add' | 'edit'
  scope: EnvVarScope
  initial?: Partial<EnvVarDialogValues>
  lockKey?: boolean
}

/**
 * Project-first environment variables. Defaults are stored at project scope and
 * apply to every branch; optional branch overrides are listed separately when
 * a branch context is available.
 */
export function EnvironmentTab({ projectId, branchId }: EnvironmentTabProps) {
  const { showSuccess, showError } = useNotification()

  const projectEnv = useProjectEnvVars(projectId, true, branchId)
  const branchEnv = useBranchEnvVars(projectId, branchId ?? '', !!branchId)

  const [dialog, setDialog] = useState<DialogState | null>(null)
  const [pasteOpen, setPasteOpen] = useState(false)
  const [deleteTarget, setDeleteTarget] = useState<EnvVarListItem | null>(null)

  const branchOverrideKeys = useMemo(
    () => new Set(branchEnv.overrideItems.map((item) => item.key)),
    [branchEnv.overrideItems],
  )

  const missingItems: RequiredEnvStatusItem[] = useMemo(() => {
    if (!branchEnv.status) return []
    const missingSet = new Set(branchEnv.status.missing)
    const fromRequired = branchEnv.status.required.filter(
      (r) => !r.satisfied && missingSet.has(r.key),
    )
    const covered = new Set(fromRequired.map((r) => r.key))
    const bareMissing: RequiredEnvStatusItem[] = branchEnv.status.missing
      .filter((k) => !covered.has(k))
      .map((k) => ({ service: 'Unknown', key: k, satisfied: false }))
    return [...fromRequired, ...bareMissing]
  }, [branchEnv.status])

  const satisfiedRequired = useMemo(
    () => (branchEnv.status?.required ?? []).filter((r) => r.satisfied),
    [branchEnv.status],
  )

  const hasMissing = missingItems.length > 0
  const hasAnyRequired = (branchEnv.status?.required.length ?? 0) > 0
  const hasSuggested = (branchEnv.status?.suggested?.length ?? 0) > 0
  const warnings = branchEnv.status?.warnings ?? []

  const isLoading = projectEnv.isLoading || (!!branchId && branchEnv.isLoading)
  const isError = projectEnv.isError || (!!branchId && branchEnv.isError)
  const isMutating =
    projectEnv.isAdding ||
    projectEnv.isUpdating ||
    projectEnv.isDeleting ||
    branchEnv.isAdding ||
    branchEnv.isUpdating ||
    branchEnv.isDeleting

  const handleSubmit = async (values: EnvVarDialogValues) => {
    const scope = dialog?.scope ?? 'project'
    if (scope === 'branch') {
      if (!branchId) return
      if (dialog?.mode === 'edit') {
        await branchEnv.updateVar(values.key, values.value, values.isSecret)
      } else {
        await branchEnv.addVar(values.key, values.value, values.isSecret)
      }
      showSuccess(
        dialog?.mode === 'edit'
          ? `Updated branch override for ${values.key}`
          : `Set branch override for ${values.key}`,
      )
    } else if (dialog?.mode === 'edit') {
      await projectEnv.updateVar(values.key, values.value)
      showSuccess(`Updated ${values.key}`)
    } else {
      await projectEnv.addVar(values.key, values.value)
      showSuccess(`Set ${values.key}`)
    }
    setDialog(null)
  }

  const handleDelete = async () => {
    if (!deleteTarget) return
    try {
      if (deleteTarget.scope === 'branch') {
        if (!branchId) return
        await branchEnv.deleteVar(deleteTarget.key)
        showSuccess(`Removed branch override for ${deleteTarget.key}`)
      } else {
        await projectEnv.deleteVar(deleteTarget.key)
        showSuccess(`Removed ${deleteTarget.key}`)
      }
      setDeleteTarget(null)
    } catch {
      showError(`Couldn't remove ${deleteTarget.key}`)
    }
  }

  const handlePasteImport = async (text: string) => {
    const parsed = parseDotEnv(text)
    if (parsed.entries.length === 0) {
      throw new Error('no entries')
    }

    const entries = parsed.entries.map((entry) => ({
      key: entry.key,
      value: entry.value,
    }))

    await projectEnv.importVars(entries)

    const skippedNote =
      parsed.skipped.length > 0 ? ` (${parsed.skipped.length} lines skipped)` : ''
    showSuccess(
      `Imported ${entries.length} project variable${entries.length === 1 ? '' : 's'}${skippedNote}`,
    )
    setPasteOpen(false)
  }

  const openProjectAdd = () => setDialog({ mode: 'add', scope: 'project' })
  const openBranchAdd = () => {
    if (!branchId) return
    setDialog({ mode: 'add', scope: 'branch' })
  }

  const openSetValue = (key: string, isSecret: boolean, scope: EnvVarScope = 'project') => {
    setDialog({
      mode: 'add',
      scope,
      lockKey: true,
      initial: { key, isSecret },
    })
  }

  return (
    <Stack spacing={3}>
      <Header
        action={
          <Stack direction="row" spacing={1}>
            <Button
              variant="pillOutlined"
              color="primary"
              startIcon={<ContentPasteIcon sx={{ fontSize: 16 }} />}
              onClick={() => setPasteOpen(true)}
              disabled={isLoading || projectEnv.isImporting}
            >
              Paste .env
            </Button>
            <Button
              variant="pill"
              color="primary"
              startIcon={<AddIcon sx={{ fontSize: 16 }} />}
              onClick={openProjectAdd}
              disabled={isLoading || projectEnv.isImporting}
            >
              Add variable
            </Button>
          </Stack>
        }
      />

      {isLoading && (
        <Stack spacing={2}>
          <Skeleton variant="rounded" height={96} />
          <Skeleton variant="rounded" height={160} />
        </Stack>
      )}

      {!isLoading && isError && (
        <Alert severity="error" variant="quiet">
          Couldn't load environment variables. Reload the page to try again.
        </Alert>
      )}

      {!isLoading && !isError && (
        <>
          {branchId && warnings.length > 0 && (
            <Alert severity="warning" variant="quiet">
              {warnings.map((warning) => (
                <Typography key={warning} sx={{ fontSize: '0.8125rem' }}>
                  {warning}
                </Typography>
              ))}
            </Alert>
          )}

          {branchId && hasMissing && (
            <Box
              sx={{
                border: `1px solid ${semanticTokens.error}`,
                borderRadius: 2,
                overflow: 'hidden',
              }}
            >
              <Stack
                direction="row"
                alignItems="center"
                spacing={1}
                sx={{
                  px: 2,
                  py: 1.25,
                  backgroundColor: semanticTokens.errorSoft,
                  borderBottom: `1px solid ${semanticTokens.error}`,
                }}
              >
                <ErrorOutlineIcon sx={{ fontSize: 18, color: semanticTokens.error }} />
                <Typography
                  sx={{
                    fontSize: '0.8125rem',
                    fontWeight: 600,
                    color: surfaceTokens.textPrimary,
                  }}
                >
                  {missingItems.length} required variable
                  {missingItems.length === 1 ? '' : 's'} not set on this branch
                </Typography>
              </Stack>
              <Stack>
                {missingItems.map((item, idx) => (
                  <MissingRequiredRow
                    key={item.key}
                    item={item}
                    first={idx === 0}
                    onSetValue={() => openSetValue(item.key, item.secret ?? true, 'project')}
                  />
                ))}
              </Stack>
            </Box>
          )}

          {branchId && !hasMissing && hasAnyRequired && (
            <Stack
              direction="row"
              alignItems="center"
              spacing={1}
              sx={{
                px: 2,
                py: 1.25,
                border: `1px solid ${surfaceTokens.hairline}`,
                borderRadius: 2,
                backgroundColor: semanticTokens.successSoft,
              }}
            >
              <CheckCircleOutlineIcon sx={{ fontSize: 18, color: semanticTokens.success }} />
              <Typography sx={{ fontSize: '0.8125rem', color: surfaceTokens.textPrimary }}>
                All {satisfiedRequired.length} required variable
                {satisfiedRequired.length === 1 ? '' : 's'} satisfied on this branch.
              </Typography>
            </Stack>
          )}

          {branchId && hasSuggested && (
            <Box
              sx={{
                border: `1px solid ${surfaceTokens.hairline}`,
                borderRadius: 2,
                overflow: 'hidden',
              }}
            >
              <Stack
                direction="row"
                alignItems="center"
                spacing={1}
                sx={{
                  px: 2,
                  py: 1.25,
                  backgroundColor: surfaceTokens.chromeBg,
                  borderBottom: `1px solid ${surfaceTokens.hairline}`,
                }}
              >
                <Typography
                  sx={{
                    fontSize: '0.8125rem',
                    fontWeight: 600,
                    color: surfaceTokens.textPrimary,
                  }}
                >
                  Suggested variables ({branchEnv.status?.suggested.length ?? 0})
                </Typography>
              </Stack>
              <Typography sx={{ px: 2, pt: 1.25, pb: 0.5, fontSize: '0.75rem', color: workspaceText.muted }}>
                Declared by the runtime spec as optional — set them when you use that integration.
                They do not block bootstrap.
              </Typography>
              <Stack>
                {(branchEnv.status?.suggested ?? []).map((item, idx) => (
                  <SuggestedEnvRow
                    key={item.key}
                    item={item}
                    first={idx === 0}
                    onSetValue={() => openSetValue(item.key, item.secret ?? true, 'project')}
                  />
                ))}
              </Stack>
            </Box>
          )}

          <EnvVarSection
            title="Project variables"
            subtitle="Shared by every branch unless overridden."
            count={projectEnv.items.length}
            emptyMessage="No project variables yet. Add one or paste a .env file."
          >
            {projectEnv.items.map((item) => (
              <EnvVarRow
                key={item.key}
                item={item}
                projectId={projectId}
                branchId={branchId}
                onEdit={(target) =>
                  setDialog({
                    mode: 'edit',
                    scope: 'project',
                    initial: {
                      key: target.key,
                      value: '',
                      isSecret: target.isSecret,
                    },
                  })
                }
                onDelete={setDeleteTarget}
                onOverride={
                  branchId && !branchOverrideKeys.has(item.key)
                    ? (target) =>
                        openSetValue(target.key, target.isSecret, 'branch')
                    : undefined
                }
              />
            ))}
          </EnvVarSection>

          {branchId && (
            <EnvVarSection
              title="Branch overrides"
              subtitle="Only this branch uses these values instead of the project default."
              count={branchEnv.overrideItems.length}
              emptyMessage="No branch overrides. Project defaults apply."
              action={
                <Button
                  variant="pillOutlined"
                  color="primary"
                  size="small"
                  startIcon={<AddIcon sx={{ fontSize: 14 }} />}
                  onClick={openBranchAdd}
                  disabled={isMutating}
                >
                  Add override
                </Button>
              }
            >
              {branchEnv.overrideItems.map((item) => (
                <EnvVarRow
                  key={item.key}
                  item={item}
                  projectId={projectId}
                  branchId={branchId}
                  onEdit={(target) =>
                    setDialog({
                      mode: 'edit',
                      scope: 'branch',
                      initial: {
                        key: target.key,
                        value: target.isSecret ? '' : (target.value ?? ''),
                        isSecret: target.isSecret,
                      },
                    })
                  }
                  onDelete={setDeleteTarget}
                />
              ))}
            </EnvVarSection>
          )}
        </>
      )}

      <EnvVarDialog
        open={dialog !== null}
        mode={dialog?.mode ?? 'add'}
        scope={dialog?.scope ?? 'project'}
        initial={dialog?.initial}
        lockKey={dialog?.lockKey}
        isSubmitting={isMutating}
        onClose={() => setDialog(null)}
        onSubmit={handleSubmit}
      />

      <DeleteEnvVarDialog
        open={deleteTarget !== null}
        envKey={deleteTarget?.key ?? null}
        scope={deleteTarget?.scope ?? 'project'}
        isDeleting={projectEnv.isDeleting || branchEnv.isDeleting}
        onClose={() => setDeleteTarget(null)}
        onConfirm={handleDelete}
      />

      <PasteEnvDialog
        open={pasteOpen}
        isImporting={projectEnv.isImporting}
        onClose={() => setPasteOpen(false)}
        onImport={handlePasteImport}
      />
    </Stack>
  )
}

function EnvVarSection({
  title,
  subtitle,
  count,
  emptyMessage,
  action,
  children,
}: {
  title: string
  subtitle: string
  count: number
  emptyMessage: string
  action?: React.ReactNode
  children: React.ReactNode
}) {
  const hasRows = count > 0

  return (
    <Box
      sx={{
        border: `1px solid ${surfaceTokens.hairline}`,
        borderRadius: 2,
        overflow: 'hidden',
      }}
    >
      <Stack
        direction={{ xs: 'column', sm: 'row' }}
        alignItems={{ xs: 'flex-start', sm: 'center' }}
        justifyContent="space-between"
        spacing={1}
        sx={{
          px: 2,
          py: 1.25,
          backgroundColor: surfaceTokens.chromeBg,
          borderBottom: hasRows ? `1px solid ${surfaceTokens.hairline}` : undefined,
        }}
      >
        <Box>
          <Typography
            sx={{
              fontSize: '0.6875rem',
              fontWeight: 600,
              letterSpacing: '0.06em',
              textTransform: 'uppercase',
              color: workspaceText.faint,
            }}
          >
            {title} ({count})
          </Typography>
          <Typography sx={{ fontSize: '0.75rem', color: workspaceText.muted, mt: 0.25 }}>
            {subtitle}
          </Typography>
        </Box>
        {action}
      </Stack>
      {hasRows ? (
        children
      ) : (
        <Box sx={{ px: 2, py: 3, textAlign: 'center' }}>
          <Typography sx={{ fontSize: '0.8125rem', color: workspaceText.muted }}>
            {emptyMessage}
          </Typography>
        </Box>
      )}
    </Box>
  )
}

function Header({ action }: { action?: React.ReactNode }) {
  return (
    <Stack
      direction="row"
      alignItems="flex-start"
      justifyContent="space-between"
      spacing={2}
    >
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
          Environment
        </Typography>
        <Typography sx={bodySx}>
          Project variables are the default for every branch. Override a key only when
          this branch needs a different value. Secret values are encrypted at rest and
          masked here — reveal them on demand. Paste a{' '}
          <Box component="span" sx={{ fontFamily: workspaceFontFamily.mono, fontSize: '0.875em' }}>
            .env
          </Box>{' '}
          file to import project defaults. Required variables block service start until
          set; suggested variables are optional integrations from the runtime spec.
        </Typography>
      </Box>
      {action && <Box sx={{ flexShrink: 0, pt: 0.5 }}>{action}</Box>}
    </Stack>
  )
}

function MissingRequiredRow({
  item,
  first,
  onSetValue,
}: {
  item: RequiredEnvStatusItem
  first: boolean
  onSetValue: () => void
}) {
  return (
    <Box
      sx={{
        display: 'grid',
        gridTemplateColumns: { xs: '1fr', sm: '1fr auto' },
        alignItems: 'center',
        gap: 1.5,
        px: 2,
        py: 1.5,
        borderTop: first ? 'none' : `1px solid ${surfaceTokens.hairline}`,
      }}
    >
      <Stack spacing={0.5} sx={{ minWidth: 0 }}>
        <Stack direction="row" alignItems="center" spacing={1} sx={{ minWidth: 0, flexWrap: 'wrap' }}>
          <Box
            component="span"
            sx={{
              fontFamily: workspaceFontFamily.mono,
              fontSize: '0.8125rem',
              fontWeight: 600,
              color: surfaceTokens.textPrimary,
            }}
          >
            {item.key}
          </Box>
          <Box
            component="span"
            sx={{
              fontSize: '0.625rem',
              fontWeight: 600,
              letterSpacing: '0.04em',
              textTransform: 'uppercase',
              color: semanticTokens.error,
              border: `1px solid ${semanticTokens.error}`,
              borderRadius: 1,
              px: 0.75,
              py: 0.125,
            }}
          >
            Required — not set
          </Box>
        </Stack>
        <Typography sx={{ fontSize: '0.75rem', color: workspaceText.muted, lineHeight: 1.45 }}>
          <Box component="span" sx={{ fontWeight: 600, color: workspaceText.primary }}>
            {item.service}
          </Box>
          {item.description ? ` — ${item.description}` : ''}
        </Typography>
      </Stack>
      <Box sx={{ justifySelf: { xs: 'start', sm: 'end' } }}>
        <Button variant="pillOutlined" color="primary" onClick={onSetValue} sx={{ minWidth: 96 }}>
          Set value
        </Button>
      </Box>
    </Box>
  )
}

function SuggestedEnvRow({
  item,
  first,
  onSetValue,
}: {
  item: SuggestedEnvStatusItem
  first: boolean
  onSetValue: () => void
}) {
  return (
    <Box
      sx={{
        display: 'grid',
        gridTemplateColumns: { xs: '1fr', sm: '1fr auto' },
        alignItems: 'center',
        gap: 1.5,
        px: 2,
        py: 1.5,
        borderTop: first ? 'none' : `1px solid ${surfaceTokens.hairline}`,
      }}
    >
      <Stack spacing={0.5} sx={{ minWidth: 0 }}>
        <Stack direction="row" alignItems="center" spacing={1} sx={{ minWidth: 0, flexWrap: 'wrap' }}>
          <Box
            component="span"
            sx={{
              fontFamily: workspaceFontFamily.mono,
              fontSize: '0.8125rem',
              fontWeight: 600,
              color: surfaceTokens.textPrimary,
            }}
          >
            {item.key}
          </Box>
          <Box
            component="span"
            sx={{
              fontSize: '0.625rem',
              fontWeight: 600,
              letterSpacing: '0.04em',
              textTransform: 'uppercase',
              color: item.satisfied ? semanticTokens.success : workspaceText.muted,
              border: `1px solid ${item.satisfied ? semanticTokens.success : surfaceTokens.hairline}`,
              borderRadius: 1,
              px: 0.75,
              py: 0.125,
            }}
          >
            {item.satisfied ? 'Optional — set' : 'Optional'}
          </Box>
        </Stack>
        <Typography sx={{ fontSize: '0.75rem', color: workspaceText.muted, lineHeight: 1.45 }}>
          <Box component="span" sx={{ fontWeight: 600, color: workspaceText.primary }}>
            {item.service}
          </Box>
          {item.description ? ` — ${item.description}` : ''}
        </Typography>
      </Stack>
      {!item.satisfied && (
        <Box sx={{ justifySelf: { xs: 'start', sm: 'end' } }}>
          <Button variant="pillOutlined" color="primary" onClick={onSetValue} sx={{ minWidth: 96 }}>
            Set value
          </Button>
        </Box>
      )}
    </Box>
  )
}

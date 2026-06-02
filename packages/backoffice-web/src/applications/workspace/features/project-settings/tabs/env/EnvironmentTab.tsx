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
  type BranchEnvVarItem,
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
import { EnvVarRow } from './EnvVarRow'
import { EnvVarDialog, type EnvVarDialogValues } from './EnvVarDialog'
import { DeleteEnvVarDialog } from './DeleteEnvVarDialog'
import { PasteEnvDialog } from './PasteEnvDialog'
import { parseDotEnv } from './parseDotEnv'

interface EnvironmentTabProps {
  projectId: string
  /** Required for branch-scoped env vars; the tab is gated on its presence. */
  branchId?: string
}

interface DialogState {
  mode: 'add' | 'edit'
  initial?: Partial<EnvVarDialogValues>
  lockKey?: boolean
}

/**
 * Branch-scoped Environment Variables tab.
 *
 * <p>Top: a prominent "missing required" section driven by the status query —
 * each missing key shows its declaring service + description and a "Set value"
 * button that opens the Add dialog prefilled. Below: the full env-var list with
 * scope chips, secret reveal, and edit/delete. All mutations invalidate both the
 * list and status queries so the missing badges clear automatically.</p>
 */
export function EnvironmentTab({ projectId, branchId }: EnvironmentTabProps) {
  const { showSuccess, showError } = useNotification()

  const env = useBranchEnvVars(projectId, branchId ?? '', !!branchId)

  const [dialog, setDialog] = useState<DialogState | null>(null)
  const [pasteOpen, setPasteOpen] = useState(false)
  const [deleteTarget, setDeleteTarget] = useState<BranchEnvVarItem | null>(null)

  const missingItems: RequiredEnvStatusItem[] = useMemo(() => {
    if (!env.status) return []
    const missingSet = new Set(env.status.missing)
    // Prefer the rich required[] rows (they carry service + description) for the
    // missing keys; fall back to a bare key if the status only lists it in
    // `missing` without a matching required entry.
    const fromRequired = env.status.required.filter(
      (r) => !r.satisfied && missingSet.has(r.key),
    )
    const covered = new Set(fromRequired.map((r) => r.key))
    const bareMissing: RequiredEnvStatusItem[] = env.status.missing
      .filter((k) => !covered.has(k))
      .map((k) => ({ service: 'Unknown', key: k, satisfied: false }))
    return [...fromRequired, ...bareMissing]
  }, [env.status])

  const satisfiedRequired = useMemo(
    () => (env.status?.required ?? []).filter((r) => r.satisfied),
    [env.status],
  )

  const hasMissing = missingItems.length > 0
  const hasAnyRequired = (env.status?.required.length ?? 0) > 0
  const hasSuggested = (env.status?.suggested?.length ?? 0) > 0
  const warnings = env.status?.warnings ?? []

  // ── Mutation runners with toasts ────────────────────────────────────────────

  const handleSubmit = async (values: EnvVarDialogValues) => {
    if (!branchId) return
    if (dialog?.mode === 'edit') {
      await env.updateVar(values.key, values.value, values.isSecret)
      showSuccess(`Updated ${values.key}`)
    } else {
      await env.addVar(values.key, values.value, values.isSecret)
      showSuccess(`Set ${values.key}`)
    }
    setDialog(null)
  }

  const handleDelete = async () => {
    if (!deleteTarget) return
    try {
      await env.deleteVar(deleteTarget.key)
      showSuccess(`Removed ${deleteTarget.key}`)
      setDeleteTarget(null)
    } catch {
      showError(`Couldn't remove ${deleteTarget.key}`)
    }
  }

  const handlePasteImport = async (text: string) => {
    if (!branchId) return
    const parsed = parseDotEnv(text)
    if (parsed.entries.length === 0) {
      throw new Error('no entries')
    }

    const secretByKey = new Map(
      [...(env.status?.required ?? []), ...(env.status?.suggested ?? [])].map(
        (item) => [item.key, item.secret ?? true],
      ),
    )
    const branchOverrideKeys = new Set(
      env.items.filter((item) => item.scope === 'branch').map((item) => item.key),
    )
    const entries = parsed.entries.map((entry) => ({
      ...entry,
      isSecret: secretByKey.get(entry.key) ?? true,
    }))

    await env.importVars(entries, branchOverrideKeys)

    const skippedNote =
      parsed.skipped.length > 0 ? ` (${parsed.skipped.length} lines skipped)` : ''
    showSuccess(
      `Imported ${entries.length} variable${entries.length === 1 ? '' : 's'}${skippedNote}`,
    )
    setPasteOpen(false)
  }

  // ── No branch context ──────────────────────────────────────────────────────

  if (!branchId) {
    return (
      <Stack spacing={3}>
        <Header />
        <Alert severity="info" variant="quiet">
          Open a branch to manage its environment variables.
        </Alert>
      </Stack>
    )
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
              disabled={env.isLoading || env.isImporting}
            >
              Paste .env
            </Button>
            <Button
              variant="pill"
              color="primary"
              startIcon={<AddIcon sx={{ fontSize: 16 }} />}
              onClick={() => setDialog({ mode: 'add' })}
              disabled={env.isLoading || env.isImporting}
            >
              Add variable
            </Button>
          </Stack>
        }
      />

      {env.isLoading && (
        <Stack spacing={2}>
          <Skeleton variant="rounded" height={96} />
          <Skeleton variant="rounded" height={160} />
        </Stack>
      )}

      {!env.isLoading && env.isError && (
        <Alert severity="error" variant="quiet">
          Couldn't load environment variables. Reload the page to try again.
        </Alert>
      )}

      {!env.isLoading && !env.isError && (
        <>
          {warnings.length > 0 && (
            <Alert severity="warning" variant="quiet">
              {warnings.map((warning) => (
                <Typography key={warning} sx={{ fontSize: '0.8125rem' }}>
                  {warning}
                </Typography>
              ))}
            </Alert>
          )}

          {/* Missing-required — prominent, only when something is actually missing */}
          {hasMissing && (
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
                  {missingItems.length === 1 ? '' : 's'} not set
                </Typography>
              </Stack>
              <Stack>
                {missingItems.map((item, idx) => (
                  <MissingRequiredRow
                    key={item.key}
                    item={item}
                    first={idx === 0}
                    onSetValue={() =>
                      setDialog({
                        mode: 'add',
                        lockKey: true,
                        initial: { key: item.key, isSecret: item.secret ?? true },
                      })
                    }
                  />
                ))}
              </Stack>
            </Box>
          )}

          {/* All required satisfied — calm confirmation, only if there were any */}
          {!hasMissing && hasAnyRequired && (
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
                {satisfiedRequired.length === 1 ? '' : 's'} satisfied.
              </Typography>
            </Stack>
          )}

          {hasSuggested && (
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
                  Suggested variables ({env.status?.suggested.length ?? 0})
                </Typography>
              </Stack>
              <Typography sx={{ px: 2, pt: 1.25, pb: 0.5, fontSize: '0.75rem', color: workspaceText.muted }}>
                Declared by the runtime spec as optional — set them when you use that integration.
                They do not block bootstrap.
              </Typography>
              <Stack>
                {(env.status?.suggested ?? []).map((item, idx) => (
                  <SuggestedEnvRow
                    key={item.key}
                    item={item}
                    first={idx === 0}
                    onSetValue={() =>
                      setDialog({
                        mode: 'add',
                        lockKey: true,
                        initial: { key: item.key, isSecret: item.secret ?? true },
                      })
                    }
                  />
                ))}
              </Stack>
            </Box>
          )}

          {/* Full list */}
          <Box
            sx={{
              border: `1px solid ${surfaceTokens.hairline}`,
              borderRadius: 2,
              overflow: 'hidden',
            }}
          >
            <Box
              sx={{
                px: 2,
                py: 1.25,
                backgroundColor: surfaceTokens.chromeBg,
              }}
            >
              <Typography
                sx={{
                  fontSize: '0.6875rem',
                  fontWeight: 600,
                  letterSpacing: '0.06em',
                  textTransform: 'uppercase',
                  color: workspaceText.faint,
                }}
              >
                Variables ({env.items.length})
              </Typography>
            </Box>
            {env.items.length === 0 ? (
              <Box sx={{ px: 2, py: 4, textAlign: 'center' }}>
                <Typography sx={{ fontSize: '0.8125rem', color: workspaceText.muted }}>
                  No environment variables yet.
                </Typography>
              </Box>
            ) : (
              env.items.map((item) => (
                <EnvVarRow
                  key={item.key}
                  item={item}
                  projectId={projectId}
                  branchId={branchId}
                  onEdit={(target) =>
                    setDialog({
                      mode: 'edit',
                      initial: {
                        key: target.key,
                        // Non-secret values are already returned and safe to seed.
                        value: target.isSecret ? '' : (target.value ?? ''),
                        isSecret: target.isSecret,
                      },
                    })
                  }
                  onDelete={(target) => setDeleteTarget(target)}
                />
              ))
            )}
          </Box>
        </>
      )}

      <EnvVarDialog
        open={dialog !== null}
        mode={dialog?.mode ?? 'add'}
        initial={dialog?.initial}
        lockKey={dialog?.lockKey}
        isSubmitting={env.isAdding || env.isUpdating}
        onClose={() => setDialog(null)}
        onSubmit={handleSubmit}
      />

      <DeleteEnvVarDialog
        open={deleteTarget !== null}
        envKey={deleteTarget?.key ?? null}
        isDeleting={env.isDeleting}
        onClose={() => setDeleteTarget(null)}
        onConfirm={handleDelete}
      />

      <PasteEnvDialog
        open={pasteOpen}
        isImporting={env.isImporting}
        onClose={() => setPasteOpen(false)}
        onImport={handlePasteImport}
      />
    </Stack>
  )
}

// ── Header ────────────────────────────────────────────────────────────────────

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
          Branch-scoped environment variables for this runtime. Secret values are
          encrypted at rest and masked here — reveal them on demand. Paste a{' '}
          <Box component="span" sx={{ fontFamily: workspaceFontFamily.mono, fontSize: '0.875em' }}>
            .env
          </Box>{' '}
          file to import many at once. Required variables block service start until
          set; suggested variables are optional integrations from the runtime spec.
        </Typography>
      </Box>
      {action && <Box sx={{ flexShrink: 0, pt: 0.5 }}>{action}</Box>}
    </Stack>
  )
}

// ── Missing-required row ────────────────────────────────────────────────────

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

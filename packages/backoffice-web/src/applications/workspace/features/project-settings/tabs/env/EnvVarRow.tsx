import { useState } from 'react'
import {
  Box,
  Chip,
  CircularProgress,
  IconButton,
  Stack,
  Tooltip,
  Typography,
} from '@mui/material'
import DeleteOutlineIcon from '@mui/icons-material/DeleteOutline'
import EditOutlinedIcon from '@mui/icons-material/EditOutlined'
import ForkRightIcon from '@mui/icons-material/ForkRight'
import VisibilityOutlinedIcon from '@mui/icons-material/VisibilityOutlined'
import VisibilityOffOutlinedIcon from '@mui/icons-material/VisibilityOffOutlined'
import {
  useGetApiProjectsProjectIdBranchesBranchIdEnvKeyReveal,
  useGetApiProjectsProjectIdSecretsKeyReveal,
} from '../../../../../../api/queries-commands'
import {
  chromeTokens,
  semanticTokens,
  surfaceTokens,
  workspaceFontFamily,
} from '../../../../shared/designTokens'
import type { EnvVarListItem, EnvVarScope } from './envVarTypes'

const tokens = {
  primary: surfaceTokens.textPrimary,
  muted: surfaceTokens.textMuted,
  faint: surfaceTokens.textFaint,
  hairline: surfaceTokens.hairline,
  rowHover: chromeTokens.rowHover,
  danger: semanticTokens.danger,
  chipBg: surfaceTokens.chipBg,
} as const

const MASK = '••••••••'

function formatUpdatedAt(iso: string): string {
  const d = new Date(iso)
  if (Number.isNaN(d.getTime())) return iso
  return d.toLocaleString(undefined, {
    year: 'numeric',
    month: 'short',
    day: 'numeric',
    hour: '2-digit',
    minute: '2-digit',
  })
}

export interface EnvVarRowProps {
  item: EnvVarListItem
  projectId: string
  branchId?: string
  onEdit: (item: EnvVarListItem) => void
  onDelete: (item: EnvVarListItem) => void
  /** When set, shows an action to create a branch-specific override for this key. */
  onOverride?: (item: EnvVarListItem) => void
}

function useRevealQuery(
  projectId: string,
  branchId: string | undefined,
  scope: EnvVarScope,
  key: string,
  enabled: boolean,
) {
  const projectReveal = useGetApiProjectsProjectIdSecretsKeyReveal(projectId, key, {
    query: { enabled: enabled && scope === 'project', staleTime: 30_000 },
  })
  const branchReveal = useGetApiProjectsProjectIdBranchesBranchIdEnvKeyReveal(
    projectId,
    branchId ?? '',
    key,
    {
      query: {
        enabled: enabled && scope === 'branch' && !!branchId,
        staleTime: 30_000,
      },
    },
  )
  return scope === 'project' ? projectReveal : branchReveal
}

export function EnvVarRow({
  item,
  projectId,
  branchId,
  onEdit,
  onDelete,
  onOverride,
}: EnvVarRowProps) {
  const [revealed, setRevealed] = useState(false)

  const revealQuery = useRevealQuery(
    projectId,
    branchId,
    item.scope,
    item.key,
    revealed && item.isSecret,
  )

  const plaintext = revealQuery.data?.plaintext

  const renderValue = () => {
    if (!item.isSecret) {
      return (
        <Box
          component="span"
          sx={{
            fontFamily: workspaceFontFamily.mono,
            fontSize: '0.8125rem',
            color: tokens.primary,
            wordBreak: 'break-all',
          }}
        >
          {item.value ?? ''}
        </Box>
      )
    }

    if (revealed) {
      if (revealQuery.isLoading) {
        return <CircularProgress size={12} sx={{ color: tokens.muted }} />
      }
      if (revealQuery.isError) {
        return (
          <Box component="span" sx={{ fontSize: '0.75rem', color: tokens.danger }}>
            Couldn't reveal value
          </Box>
        )
      }
      return (
        <Box
          component="span"
          sx={{
            fontFamily: workspaceFontFamily.mono,
            fontSize: '0.8125rem',
            color: tokens.primary,
            wordBreak: 'break-all',
          }}
        >
          {plaintext}
        </Box>
      )
    }

    return (
      <Box
        component="span"
        sx={{
          fontFamily: workspaceFontFamily.mono,
          fontSize: '0.8125rem',
          color: tokens.faint,
          letterSpacing: '0.08em',
        }}
      >
        {MASK}
      </Box>
    )
  }

  const scopeLabel =
    item.scope === 'project' ? 'Project default' : 'Branch override'

  return (
    <Box
      sx={{
        display: 'grid',
        gridTemplateColumns: { xs: '1fr', sm: 'minmax(0, 1.2fr) minmax(0, 1.4fr) auto' },
        alignItems: 'center',
        gap: 1.5,
        px: 2,
        py: 1.5,
        borderTop: `1px solid ${tokens.hairline}`,
        transition: 'background-color 120ms ease',
        '&:hover': { backgroundColor: tokens.rowHover },
      }}
    >
      <Stack spacing={0.5} sx={{ minWidth: 0 }}>
        <Stack direction="row" alignItems="center" spacing={1} sx={{ minWidth: 0 }}>
          <Box
            component="span"
            sx={{
              fontFamily: workspaceFontFamily.mono,
              fontSize: '0.8125rem',
              fontWeight: 600,
              color: tokens.primary,
              overflow: 'hidden',
              textOverflow: 'ellipsis',
              whiteSpace: 'nowrap',
            }}
            title={item.key}
          >
            {item.key}
          </Box>
          <Chip
            label={scopeLabel}
            size="small"
            variant="outlined"
            sx={{
              height: 18,
              fontSize: '0.625rem',
              letterSpacing: '0.04em',
              textTransform: 'uppercase',
              color: tokens.muted,
              borderColor: tokens.hairline,
            }}
          />
        </Stack>
        <Typography sx={{ fontSize: '0.6875rem', color: tokens.faint }}>
          Updated {formatUpdatedAt(item.updatedAt)}
        </Typography>
      </Stack>

      <Stack direction="row" alignItems="center" spacing={1} sx={{ minWidth: 0 }}>
        <Box sx={{ minWidth: 0, flex: 1, overflow: 'hidden' }}>{renderValue()}</Box>
        {item.isSecret && (
          <Tooltip title={revealed ? 'Hide' : 'Reveal'}>
            <IconButton
              size="small"
              aria-label={revealed ? `Hide ${item.key}` : `Reveal ${item.key}`}
              onClick={() => setRevealed((v) => !v)}
              sx={{ color: tokens.muted, '&:hover': { color: tokens.primary } }}
            >
              {revealed ? (
                <VisibilityOffOutlinedIcon sx={{ fontSize: 16 }} />
              ) : (
                <VisibilityOutlinedIcon sx={{ fontSize: 16 }} />
              )}
            </IconButton>
          </Tooltip>
        )}
      </Stack>

      <Stack direction="row" spacing={0.5} sx={{ justifySelf: { xs: 'start', sm: 'end' } }}>
        {onOverride && (
          <Tooltip title="Override for this branch">
            <IconButton
              size="small"
              aria-label={`Override ${item.key} for this branch`}
              onClick={() => onOverride(item)}
              sx={{ color: tokens.muted, '&:hover': { color: tokens.primary } }}
            >
              <ForkRightIcon sx={{ fontSize: 16 }} />
            </IconButton>
          </Tooltip>
        )}
        <Tooltip title="Edit">
          <IconButton
            size="small"
            aria-label={`Edit ${item.key}`}
            onClick={() => onEdit(item)}
            sx={{ color: tokens.muted, '&:hover': { color: tokens.primary } }}
          >
            <EditOutlinedIcon sx={{ fontSize: 16 }} />
          </IconButton>
        </Tooltip>
        <Tooltip title="Delete">
          <IconButton
            size="small"
            aria-label={`Delete ${item.key}`}
            onClick={() => onDelete(item)}
            sx={{ color: tokens.muted, '&:hover': { color: tokens.danger } }}
          >
            <DeleteOutlineIcon sx={{ fontSize: 16 }} />
          </IconButton>
        </Tooltip>
      </Stack>
    </Box>
  )
}

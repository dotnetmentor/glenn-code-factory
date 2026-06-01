import { useEffect, useMemo, useState } from 'react'
import {
  Box,
  Button,
  Chip,
  MenuItem,
  Select,
  Stack,
  TextField,
  Typography,
} from '@mui/material'
import { useQueryClient } from '@tanstack/react-query'
import {
  getGetApiProjectsProjectIdQueryKey,
  useGetApiProjectsProjectId,
  usePatchApiProjectsProjectIdRuntimeSpec,
} from '../../../../../api/queries-commands'
import { useNotification } from '../../../../shared/contexts/NotificationContext'
import {
  bodySx,
  captionSx,
  sectionTitleSx,
  workspaceAccent,
  workspaceFontFamily,
  workspaceText,
} from '../../../shared'

// Mirror Project.cs constants — keep these in lockstep with the backend's
// validation rules in Project.SetRuntimeSpec(...). The handler is the source
// of truth; these are just for friendly client-side gating.
const CPU_KINDS = ['shared', 'performance'] as const
type CpuKind = (typeof CPU_KINDS)[number]

const ALLOWED_CPUS = [1, 2, 4, 8, 16] as const
const MIN_MEMORY_MB = 256
const MAX_MEMORY_MB = 262144
const MIN_VOLUME_GB = 1
const MAX_VOLUME_GB = 500

// Fly's hard floor for the performance class: memoryMb >= 2048 * cpus.
const PERFORMANCE_MIN_RAM_PER_CPU = 2048

const DEFAULT_CPU_KIND: CpuKind = 'shared'
const DEFAULT_CPUS = 1
const DEFAULT_MEMORY_MB = 2048
const DEFAULT_VOLUME_GB = 5

interface PerformanceSettingsTabProps {
  projectId: string
}

/**
 * Performance tab — lets the user lab with the project's default runtime spec
 * (CPU class, vCPU count, RAM, volume size). The new spec applies to
 * subsequently-created runtimes only (new branch, fork, attach, AI
 * onboarding); live runtimes keep the spec they booted with thanks to the
 * snapshot pattern in the backend's creation handlers.
 *
 * <p>Saving fires PATCH /api/projects/{projectId}/runtime-spec. Cross-field
 * validation (Fly's performance-class memory floor: memoryMb &gt;= 2048 * cpus)
 * is also enforced server-side by Project.SetRuntimeSpec — we mirror it here
 * for inline UX so the user sees the issue before round-tripping.</p>
 */
export function PerformanceSettingsTab({ projectId }: PerformanceSettingsTabProps) {
  const queryClient = useQueryClient()
  const { showSuccess, showError } = useNotification()

  const projectQuery = useGetApiProjectsProjectId(projectId, {
    query: { enabled: !!projectId },
  })

  const currentCpuKind = (projectQuery.data?.runtimeCpuKind ?? DEFAULT_CPU_KIND) as CpuKind
  const currentCpus = projectQuery.data?.runtimeCpus ?? DEFAULT_CPUS
  const currentMemoryMb = projectQuery.data?.runtimeMemoryMb ?? DEFAULT_MEMORY_MB
  const currentVolumeGb = projectQuery.data?.runtimeVolumeSizeGb ?? DEFAULT_VOLUME_GB

  const [cpuKind, setCpuKind] = useState<CpuKind>(currentCpuKind)
  const [cpus, setCpus] = useState<number>(currentCpus)
  const [memoryDraft, setMemoryDraft] = useState<string>(String(currentMemoryMb))
  const [volumeDraft, setVolumeDraft] = useState<string>(String(currentVolumeGb))

  // Reset the local draft whenever the upstream value changes (drawer reopen,
  // refetch after a save in another tab, SignalR push later, etc.).
  useEffect(() => {
    setCpuKind(currentCpuKind)
    setCpus(currentCpus)
    setMemoryDraft(String(currentMemoryMb))
    setVolumeDraft(String(currentVolumeGb))
  }, [currentCpuKind, currentCpus, currentMemoryMb, currentVolumeGb])

  const mutation = usePatchApiProjectsProjectIdRuntimeSpec()

  // ── Parsing + validation ────────────────────────────────────────────────
  const parsedMemoryMb = Number(memoryDraft)
  const memoryIsInteger = memoryDraft.trim() !== '' && Number.isInteger(parsedMemoryMb)
  const memoryInRange =
    memoryIsInteger && parsedMemoryMb >= MIN_MEMORY_MB && parsedMemoryMb <= MAX_MEMORY_MB

  const parsedVolumeGb = Number(volumeDraft)
  const volumeIsInteger = volumeDraft.trim() !== '' && Number.isInteger(parsedVolumeGb)
  const volumeInRange =
    volumeIsInteger && parsedVolumeGb >= MIN_VOLUME_GB && parsedVolumeGb <= MAX_VOLUME_GB

  const performanceMemoryFloor = PERFORMANCE_MIN_RAM_PER_CPU * cpus
  const performanceMemoryTooLow =
    cpuKind === 'performance' && memoryInRange && parsedMemoryMb < performanceMemoryFloor

  const memoryHelper = useMemo(() => {
    if (!memoryIsInteger || !memoryInRange) {
      return `Enter ${MIN_MEMORY_MB}–${MAX_MEMORY_MB} MB.`
    }
    if (performanceMemoryTooLow) {
      return `Performance class needs at least ${performanceMemoryFloor} MB (2048 MB × ${cpus} CPUs).`
    }
    return undefined
  }, [memoryIsInteger, memoryInRange, performanceMemoryTooLow, performanceMemoryFloor, cpus])

  const volumeHelper = useMemo(() => {
    if (!volumeIsInteger || !volumeInRange) {
      return `Enter ${MIN_VOLUME_GB}–${MAX_VOLUME_GB} GB.`
    }
    return undefined
  }, [volumeIsInteger, volumeInRange])

  const dirty =
    cpuKind !== currentCpuKind ||
    cpus !== currentCpus ||
    (memoryInRange && parsedMemoryMb !== currentMemoryMb) ||
    (volumeInRange && parsedVolumeGb !== currentVolumeGb)

  const allValid =
    memoryInRange && volumeInRange && !performanceMemoryTooLow

  const canSave = dirty && allValid && !mutation.isPending

  const handleSave = () => {
    if (!canSave) return
    mutation.mutate(
      {
        projectId,
        data: {
          cpuKind,
          cpus,
          memoryMb: parsedMemoryMb,
          volumeSizeGb: parsedVolumeGb,
        },
      },
      {
        onSuccess: () => {
          showSuccess(
            `Performance updated: ${cpuKind} / ${cpus} CPU / ${parsedMemoryMb} MB RAM / ${parsedVolumeGb} GB disk. New branches will use this spec.`,
          )
          queryClient.invalidateQueries({
            queryKey: getGetApiProjectsProjectIdQueryKey(projectId),
          })
        },
        onError: (error: unknown) => {
          const errorBody = (error as {
            response?: { data?: { detail?: string; error?: string } }
          })?.response?.data
          const code = errorBody?.detail ?? errorBody?.error
          if (code === 'performance_memory_too_low') {
            showError(
              `Performance class needs at least ${performanceMemoryFloor} MB of RAM for ${cpus} CPUs.`,
            )
          } else if (code === 'invalid_cpu_kind') {
            showError('CPU class must be "shared" or "performance".')
          } else if (code === 'invalid_cpu_count') {
            showError('CPU count must be 1, 2, 4, 8, or 16.')
          } else if (code === 'invalid_memory_mb') {
            showError(`Memory must be between ${MIN_MEMORY_MB} and ${MAX_MEMORY_MB} MB.`)
          } else if (code === 'invalid_volume_size_gb') {
            showError(`Volume size must be between ${MIN_VOLUME_GB} and ${MAX_VOLUME_GB} GB.`)
          } else {
            showError('Could not update the runtime spec.')
          }
        },
      },
    )
  }

  // Convenience: when the user switches to performance, snap memory up to the
  // floor if it would otherwise fall short. They can dial it back down — we
  // just nudge so the form isn't immediately invalid on toggle.
  const handleCpuKindChange = (value: CpuKind) => {
    setCpuKind(value)
    if (value === 'performance') {
      const floor = PERFORMANCE_MIN_RAM_PER_CPU * cpus
      if (parsedMemoryMb < floor) {
        setMemoryDraft(String(floor))
      }
    }
  }

  // Same idea when the user changes CPU count under the performance class.
  const handleCpuCountChange = (value: number) => {
    setCpus(value)
    if (cpuKind === 'performance') {
      const floor = PERFORMANCE_MIN_RAM_PER_CPU * value
      if (parsedMemoryMb < floor) {
        setMemoryDraft(String(floor))
      }
    }
  }

  return (
    <Stack spacing={4}>
      {/* Heading */}
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
          Performance
        </Typography>
        <Typography sx={bodySx}>
          The CPU, RAM and disk new runtimes for this project will boot with.
          Existing branches keep their current size — this only affects new
          branches you spin up after saving.
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
          {/* CPU class */}
          <Box>
            <Typography sx={sectionTitleSx}>CPU class</Typography>
            <Typography sx={{ ...captionSx, mt: 0.5, mb: 1.5 }}>
              <b>Shared</b> burstable, cheaper, fine for installers + dev servers.{' '}
              <b>Performance</b> dedicated cores, faster sustained work; requires
              at least 2 GB RAM per CPU.
            </Typography>
            <Select
              value={cpuKind}
              onChange={(e) => handleCpuKindChange(e.target.value as CpuKind)}
              size="small"
              sx={{
                width: 220,
                backgroundColor: 'instrument.inputBg',
                fontFamily: workspaceFontFamily.sans,
                fontSize: '0.875rem',
                color: workspaceText.primary,
              }}
            >
              {CPU_KINDS.map((kind) => (
                <MenuItem key={kind} value={kind}>
                  {kind}
                </MenuItem>
              ))}
            </Select>
          </Box>

          {/* CPU count */}
          <Box>
            <Typography sx={sectionTitleSx}>vCPU count</Typography>
            <Typography sx={{ ...captionSx, mt: 0.5, mb: 1.5 }}>
              How many virtual CPUs the runtime gets. More CPUs help parallel
              installs (npm/pnpm) and TypeScript builds.
            </Typography>
            <Select
              value={cpus}
              onChange={(e) => handleCpuCountChange(Number(e.target.value))}
              size="small"
              sx={{
                width: 140,
                backgroundColor: 'instrument.inputBg',
                fontFamily: workspaceFontFamily.sans,
                fontSize: '0.875rem',
                color: workspaceText.primary,
              }}
            >
              {ALLOWED_CPUS.map((n) => (
                <MenuItem key={n} value={n}>
                  {n}
                </MenuItem>
              ))}
            </Select>
          </Box>

          {/* Memory */}
          <Box>
            <Typography sx={sectionTitleSx}>Memory (MB)</Typography>
            <Typography sx={{ ...captionSx, mt: 0.5, mb: 1.5 }}>
              RAM allocated to the runtime. Heavy installs (large npm graphs,
              TypeScript builds) need at least 4096 MB to be comfortable.
            </Typography>
            <TextField
              value={memoryDraft}
              onChange={(e) => setMemoryDraft(e.target.value)}
              size="small"
              type="number"
              error={!!memoryHelper}
              helperText={memoryHelper}
              inputProps={{
                'aria-label': 'Memory in MB',
                min: MIN_MEMORY_MB,
                max: MAX_MEMORY_MB,
                step: 256,
                inputMode: 'numeric',
              }}
              InputProps={{
                sx: {
                  backgroundColor: 'instrument.inputBg',
                  fontFamily: workspaceFontFamily.sans,
                  fontSize: '0.875rem',
                  color: workspaceText.primary,
                },
              }}
              sx={{ width: 200 }}
            />
            <Stack direction="row" spacing={1} sx={{ mt: 1.25, flexWrap: 'wrap', gap: 1 }}>
              {[2048, 4096, 8192, 16384].map((mb) => (
                <Chip
                  key={mb}
                  label={`${mb} MB`}
                  size="small"
                  onClick={() => setMemoryDraft(String(mb))}
                  sx={{
                    cursor: 'pointer',
                    backgroundColor:
                      Number(memoryDraft) === mb
                        ? workspaceAccent.ink
                        : 'instrument.inputBg',
                    color:
                      Number(memoryDraft) === mb ? '#fff' : workspaceText.primary,
                    border: 1,
                    borderColor: 'instrument.hairline',
                    fontFamily: workspaceFontFamily.sans,
                  }}
                />
              ))}
            </Stack>
          </Box>

          {/* Volume size */}
          <Box>
            <Typography sx={sectionTitleSx}>Disk (GB)</Typography>
            <Typography sx={{ ...captionSx, mt: 0.5, mb: 1.5 }}>
              Size of the persistent volume the runtime clones the git branch
              into. node_modules + build artifacts eat space fast — for full
              app installs aim for 15–25 GB.
            </Typography>
            <TextField
              value={volumeDraft}
              onChange={(e) => setVolumeDraft(e.target.value)}
              size="small"
              type="number"
              error={!!volumeHelper}
              helperText={volumeHelper}
              inputProps={{
                'aria-label': 'Volume size in GB',
                min: MIN_VOLUME_GB,
                max: MAX_VOLUME_GB,
                step: 1,
                inputMode: 'numeric',
              }}
              InputProps={{
                sx: {
                  backgroundColor: 'instrument.inputBg',
                  fontFamily: workspaceFontFamily.sans,
                  fontSize: '0.875rem',
                  color: workspaceText.primary,
                },
              }}
              sx={{ width: 160 }}
            />
            <Stack direction="row" spacing={1} sx={{ mt: 1.25, flexWrap: 'wrap', gap: 1 }}>
              {[5, 10, 15, 25, 40].map((gb) => (
                <Chip
                  key={gb}
                  label={`${gb} GB`}
                  size="small"
                  onClick={() => setVolumeDraft(String(gb))}
                  sx={{
                    cursor: 'pointer',
                    backgroundColor:
                      Number(volumeDraft) === gb
                        ? workspaceAccent.ink
                        : 'instrument.inputBg',
                    color:
                      Number(volumeDraft) === gb ? '#fff' : workspaceText.primary,
                    border: 1,
                    borderColor: 'instrument.hairline',
                    fontFamily: workspaceFontFamily.sans,
                  }}
                />
              ))}
            </Stack>
          </Box>

          {/* Action row */}
          <Stack direction="row" justifyContent="space-between" alignItems="center" sx={{ pt: 1 }}>
            <Typography sx={captionSx}>
              Current: {currentCpuKind} / {currentCpus} CPU / {currentMemoryMb} MB /{' '}
              {currentVolumeGb} GB
            </Typography>
            <Button
              variant="pill" color="primary"
              onClick={handleSave}
              disabled={!canSave}
            >
              {mutation.isPending ? 'Saving…' : 'Save'}
            </Button>
          </Stack>
        </Stack>
      </Box>
    </Stack>
  )
}

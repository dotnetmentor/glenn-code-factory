import { useEffect, useState } from 'react'
import {
  Box,
  Drawer,
  IconButton,
  Stack,
  Tab,
  Tabs,
  Typography,
  useMediaQuery,
} from '@mui/material'
import { useTheme } from '@mui/material/styles'
import CloseIcon from '@mui/icons-material/Close'
import { useGetApiProjectsProjectId } from '../../../../api/queries-commands'
import {
  ActivityTab,
  RuntimeTab,
  ServicesTabContainer,
  useRuntimeDebugPanel,
} from '@/applications/shared/runtime'
import { ProjectGeneralTab } from './tabs/ProjectGeneralTab'
import { PermissionsTab } from './tabs/PermissionsTab'
import { ByokTab } from './tabs/ByokTab'
import { PreviewSettingsTab } from './tabs/PreviewSettingsTab'
import { PerformanceSettingsTab } from './tabs/PerformanceSettingsTab'
import { AgentSettingsTab } from './tabs/AgentSettingsTab'
import { BranchesSettingsTab } from './tabs/BranchesSettingsTab'
import { RuntimesTab } from './tabs/RuntimesTab'
import { EnvironmentTab } from './tabs/env/EnvironmentTab'

/**
 * Tab identifiers for the project-settings drawer. Each value is a stable
 * string identifier used by the {@code initialTab} prop and (later) URL hash
 * deep-linking.
 */
export const PROJECT_SETTINGS_TABS = [
  'general',
  'permissions',
  'byok',
  'environment',
  'runtime',
  'preview',
  'performance',
  'agent',
  'branches',
  'runtimes',
  'services',
  'activity',
] as const

export type ProjectSettingsTab = (typeof PROJECT_SETTINGS_TABS)[number]

interface TabDescriptor {
  value: ProjectSettingsTab
  label: string
}

const TAB_DESCRIPTORS: readonly TabDescriptor[] = [
  { value: 'general', label: 'General' },
  { value: 'permissions', label: 'Agent permissions' },
  { value: 'byok', label: 'Credentials' },
  // Environment — branch-scoped env vars with required-vs-present status. Sits
  // next to Credentials (both are per-branch config knobs) and before Runtime,
  // since the runtime's services consume these values.
  { value: 'environment', label: 'Environment' },
  { value: 'runtime', label: 'Runtime' },
  // Sits intentionally next to Runtime — they're siblings, not nested.
  // Preview is the realtime Cloudflare tunnel port; Runtime is the
  // longer-lived container spec. Different cadences, different concerns.
  { value: 'preview', label: 'Preview' },
  // Performance is the project-default CPU/RAM/disk spec used when new
  // runtimes (branches, forks, attaches) are spun up. Sibling to Runtime,
  // which views/controls the current branch's running container.
  { value: 'performance', label: 'Performance' },
  // Agent — picks which Anthropic model new turns on this project default to.
  // Sits between Performance and Services because the choice is a per-project
  // configuration knob (like Performance), not a live service rotation.
  { value: 'agent', label: 'Agent' },
  // Branches — archive/unarchive branches on this project. Sits between
  // Agent and Services because it's a per-project content management knob,
  // not a live service rotation.
  { value: 'branches', label: 'Branches' },
  // Runtimes — list of running Fly machines for this project. Lives next to
  // Branches (each runtime is bound to a branch) and before Services because
  // a runtime is the thing the services run inside.
  { value: 'runtimes', label: 'Runtimes' },
  { value: 'services', label: 'Services' },
  { value: 'activity', label: 'Activity' },
] as const

import { chromeTokens, surfaceTokens } from '../../shared/designTokens'

const tokens = { ...surfaceTokens, ...chromeTokens }

const DRAWER_WIDTH = 720

function isProjectSettingsTab(value: string): value is ProjectSettingsTab {
  return (PROJECT_SETTINGS_TABS as readonly string[]).includes(value)
}

export interface ProjectSettingsDrawerProps {
  /** Whether the drawer is currently visible. */
  open: boolean
  /** Called when the user dismisses the drawer. */
  onClose: () => void
  /** Project ID — required when {@code open} is true. */
  projectId: string
  /**
   * Current branch ID — required for branch-scoped destructive actions in the
   * General tab's "Cancel everything on this branch" affordance. When omitted
   * the affordance is suppressed (e.g. on routes that own the drawer without
   * a specific branch context).
   */
  branchId?: string
  /** Workspace slug — used for the optional post-delete redirect. */
  slug: string
  /**
   * Which tab should be selected when the drawer opens. Defaults to
   * {@code 'general'} when omitted.
   */
  initialTab?: ProjectSettingsTab
}

/**
 * Right-anchored drawer that owns project-level settings (General rename/delete,
 * Agent permissions override, and BYOK credentials). Replaces the legacy
 * MoreVert popover menu in the chat chrome and the standalone Agent Permissions
 * page route.
 *
 * <p>Modeled on {@code WorkspaceSettingsDrawer} so both surfaces feel like one
 * product — same 720px width, same chrome strip, same bronze active-edge
 * accent on the vertical Tabs nav.</p>
 */
export function ProjectSettingsDrawer({
  open,
  onClose,
  projectId,
  branchId,
  slug,
  initialTab = 'general',
}: ProjectSettingsDrawerProps) {
  const projectQuery = useGetApiProjectsProjectId(projectId, {
    query: { enabled: open && !!projectId },
  })

  // ── Responsive nav orientation ──────────────────────────────────────────
  //
  // On narrow viewports the 200px vertical left-rail eats more than half the
  // 100%-width drawer (phones are typically 360–414px wide), squeezing the
  // content surface into a useless strip. Switching to a horizontal scrollable
  // tab strip on top recovers the full width for the actual settings form.
  // Desktop is unchanged — the left rail reads as a navigation column there.
  const theme = useTheme()
  const isNarrow = useMediaQuery(theme.breakpoints.down('sm'))

  const resolvedInitial: ProjectSettingsTab = isProjectSettingsTab(initialTab)
    ? initialTab
    : 'general'

  const [selectedTab, setSelectedTab] = useState<ProjectSettingsTab>(resolvedInitial)

  // Reset the selected tab whenever the drawer (re-)opens.
  useEffect(() => {
    if (open) setSelectedTab(resolvedInitial)
  }, [open, resolvedInitial])

  const projectName = projectQuery.data?.name ?? 'Project'

  // Cross-link to the bottom log panel from the Services tab. The provider
  // for this context wraps {@code ProjectWorkspaceShell}, so the call here
  // resolves to the live panel; outside the shell it falls back to a no-op
  // (see {@code useRuntimeDebugPanel}).
  const debugPanel = useRuntimeDebugPanel()
  const handleViewLogs = (serviceName: string) => {
    onClose()
    debugPanel.openPanel(serviceName)
  }

  return (
    <Drawer
      anchor="right"
      open={open}
      onClose={onClose}
      ModalProps={{ keepMounted: false }}
      PaperProps={{
        sx: {
          width: { xs: '100%', sm: DRAWER_WIDTH },
          maxWidth: '100vw',
          backgroundColor: tokens.canvasBg,
          color: tokens.textPrimary,
          borderLeft: `1px solid ${tokens.hairline}`,
          boxShadow: 'none',
        },
      }}
    >
      <Box
        role="dialog"
        aria-label="Project settings"
        sx={{
          height: '100%',
          display: 'flex',
          flexDirection: 'column',
          overflow: 'hidden',
        }}
      >
        {/* Header — project name + close */}
        <Stack
          direction="row"
          alignItems="center"
          justifyContent="space-between"
          sx={{
            flexShrink: 0,
            px: 3,
            height: 56,
            backgroundColor: tokens.chromeBg,
            borderBottom: `1px solid ${tokens.hairline}`,
          }}
        >
          <Stack direction="row" spacing={1.5} alignItems="baseline">
            <Typography
              sx={{
                fontSize: '0.6875rem',
                fontWeight: 600,
                letterSpacing: '0.08em',
                textTransform: 'uppercase',
                color: tokens.textMuted,
              }}
            >
              Project
            </Typography>
            <Typography
              component="h2"
              sx={{
                fontSize: '0.9375rem',
                fontWeight: 500,
                letterSpacing: '-0.005em',
                color: tokens.textPrimary,
                maxWidth: 480,
                overflow: 'hidden',
                textOverflow: 'ellipsis',
                whiteSpace: 'nowrap',
              }}
              title={projectName}
            >
              {projectName}
            </Typography>
          </Stack>
          <IconButton
            size="small"
            aria-label="Close project settings"
            onClick={onClose}
            sx={{
              color: tokens.textMuted,
              transition: 'color 200ms ease',
              '&:hover': {
                color: tokens.accent,
                backgroundColor: 'transparent',
              },
            }}
          >
            <CloseIcon fontSize="small" />
          </IconButton>
        </Stack>

        {/* Body — left rail + main on desktop; stacked horizontal-tabs on
            mobile so the nav doesn't eat half the phone's viewport. */}
        <Box
          sx={{
            flex: 1,
            minHeight: 0,
            display: 'flex',
            flexDirection: { xs: 'column', sm: 'row' },
          }}
        >
          <Box
            component="nav"
            aria-label="Project settings sections"
            sx={{
              // Desktop: vertical 200px column on the left.
              // Mobile: full-width horizontal strip on top.
              width: { xs: '100%', sm: 200 },
              flexShrink: 0,
              backgroundColor: tokens.chromeBg,
              borderRight: { xs: 'none', sm: `1px solid ${tokens.hairline}` },
              borderBottom: { xs: `1px solid ${tokens.hairline}`, sm: 'none' },
              py: { xs: 0, sm: 1.5 },
              overflowX: { xs: 'auto', sm: 'visible' },
              overflowY: { xs: 'visible', sm: 'auto' },
            }}
          >
            <Tabs
              orientation={isNarrow ? 'horizontal' : 'vertical'}
              value={selectedTab}
              onChange={(_e, value: ProjectSettingsTab) => setSelectedTab(value)}
              variant={isNarrow ? 'scrollable' : 'standard'}
              scrollButtons={isNarrow ? 'auto' : false}
              allowScrollButtonsMobile
              TabIndicatorProps={{ sx: { display: 'none' } }}
              sx={{
                minHeight: 0,
                '& .MuiTabs-flexContainer': { gap: 0.25 },
              }}
            >
              {TAB_DESCRIPTORS.map((descriptor) => (
                <Tab
                  key={descriptor.value}
                  value={descriptor.value}
                  label={descriptor.label}
                  disableRipple
                  sx={{
                    alignItems: { xs: 'center', sm: 'flex-start' },
                    justifyContent: { xs: 'center', sm: 'flex-start' },
                    textAlign: { xs: 'center', sm: 'left' },
                    textTransform: 'none',
                    minHeight: { xs: 44, sm: 36 },
                    minWidth: { xs: 'auto', sm: 0 },
                    px: { xs: 2, sm: 2.5 },
                    py: { xs: 1, sm: 0.75 },
                    fontSize: '0.8125rem',
                    fontWeight: 400,
                    letterSpacing: '-0.005em',
                    color: tokens.textMuted,
                    // Mobile: bottom underline mirrors a typical horizontal
                    // tab strip. Desktop: left edge mirrors a sidebar.
                    borderLeft: {
                      xs: '0 solid transparent',
                      sm: '2px solid transparent',
                    },
                    borderBottom: {
                      xs: '2px solid transparent',
                      sm: '0 solid transparent',
                    },
                    borderRadius: 0,
                    transition:
                      'color 200ms ease, border-color 200ms ease, font-weight 200ms ease',
                    '&:hover': { color: tokens.textPrimary },
                    '&.Mui-selected': {
                      color: tokens.textPrimary,
                      fontWeight: 500,
                      borderLeftColor: { xs: 'transparent', sm: tokens.accent },
                      borderBottomColor: { xs: tokens.accent, sm: 'transparent' },
                    },
                    '&.Mui-focusVisible': {
                      outline: `2px solid ${tokens.accent}`,
                      outlineOffset: -2,
                    },
                  }}
                />
              ))}
            </Tabs>
          </Box>

          <Box
            component="main"
            sx={{
              flex: 1,
              minWidth: 0,
              overflowY: 'auto',
              // The legacy tabs (general / permissions / byok) own their own
              // top-level Stack rhythm and expect the drawer body to pad them.
              // The new runtime tabs render their own full-bleed surfaces, so
              // we drop the padding for those values.
              p:
                selectedTab === 'runtime' ||
                selectedTab === 'services' ||
                selectedTab === 'activity'
                  ? 0
                  : { xs: 3, md: 4 },
              backgroundColor: tokens.canvasBg,
              display: 'flex',
              flexDirection: 'column',
            }}
          >
            {selectedTab === 'general' && (
              <ProjectGeneralTab
                projectId={projectId}
                branchId={branchId}
                slug={slug}
                projectName={projectName}
                onDeleted={onClose}
              />
            )}
            {selectedTab === 'permissions' && (
              <PermissionsTab projectId={projectId} />
            )}
            {selectedTab === 'byok' && <ByokTab projectId={projectId} />}
            {selectedTab === 'environment' && (
              <EnvironmentTab projectId={projectId} branchId={branchId} />
            )}
            {selectedTab === 'preview' && (
              <PreviewSettingsTab projectId={projectId} />
            )}
            {selectedTab === 'performance' && (
              <PerformanceSettingsTab projectId={projectId} />
            )}
            {selectedTab === 'agent' && (
              <AgentSettingsTab projectId={projectId} />
            )}
            {selectedTab === 'branches' && (
              <BranchesSettingsTab projectId={projectId} slug={slug} />
            )}
            {selectedTab === 'runtimes' && (
              <RuntimesTab projectId={projectId} />
            )}
            {selectedTab === 'runtime' && (
              <Box sx={{ p: { xs: 3, md: 4 }, flex: 1, minHeight: 0, display: 'flex', flexDirection: 'column' }}>
                <RuntimeTab projectId={projectId} />
              </Box>
            )}
            {selectedTab === 'services' && (
              <Box sx={{ p: { xs: 3, md: 4 }, flex: 1, minHeight: 0, display: 'flex', flexDirection: 'column' }}>
                <ServicesTabContainer
                  projectId={projectId}
                  onViewLogs={handleViewLogs}
                />
              </Box>
            )}
            {selectedTab === 'activity' && (
              <Box sx={{ p: { xs: 3, md: 4 }, flex: 1, minHeight: 0, display: 'flex', flexDirection: 'column' }}>
                <ActivityTab projectId={projectId} />
              </Box>
            )}
          </Box>
        </Box>
      </Box>
    </Drawer>
  )
}

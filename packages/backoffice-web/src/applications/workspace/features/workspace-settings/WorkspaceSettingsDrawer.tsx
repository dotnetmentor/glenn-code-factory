import { useEffect, useState } from 'react'
import {
  Box,
  Drawer,
  IconButton,
  Stack,
  Tab,
  Tabs,
  Typography,
} from '@mui/material'
import CloseIcon from '@mui/icons-material/Close'
import { useWorkspace } from '../../../shared/contexts/WorkspaceContext'
import { IntegrationsTab } from './tabs/IntegrationsTab'
import { MembersTab } from './tabs/MembersTab'
import { RepositoriesTab } from './tabs/RepositoriesTab'
import { SpecsTab } from './tabs/SpecsTab'
import { WorkspaceGeneralTab } from './tabs/WorkspaceGeneralTab'

/**
 * The four tabs the workspace settings drawer exposes. Each value is also the
 * URL-hash slug used for deep-linking (e.g. {@code #settings=members}) and the
 * canonical string identifier passed via the {@code initialTab} prop.
 *
 * <p>These are the four routed pages from the legacy top-nav (Members,
 * Integrations, Repositories) plus a new General tab. The content for each is
 * filled in by parallel cards — this skeleton renders only placeholders so the
 * drawer chrome can ship independently.</p>
 */
export const WORKSPACE_SETTINGS_TABS = [
  'general',
  'members',
  'integrations',
  'repositories',
  'specs',
] as const

export type WorkspaceSettingsTab = (typeof WORKSPACE_SETTINGS_TABS)[number]

interface TabDescriptor {
  value: WorkspaceSettingsTab
  label: string
}

const TAB_DESCRIPTORS: readonly TabDescriptor[] = [
  { value: 'general', label: 'General' },
  { value: 'members', label: 'Members' },
  { value: 'integrations', label: 'Integrations' },
  { value: 'repositories', label: 'Repositories' },
  { value: 'specs', label: 'Specs' },
] as const

import { chromeTokens, surfaceTokens } from '../../shared/designTokens'

const tokens = { ...surfaceTokens, ...chromeTokens }

const DRAWER_WIDTH = 720

function isWorkspaceSettingsTab(value: string): value is WorkspaceSettingsTab {
  return (WORKSPACE_SETTINGS_TABS as readonly string[]).includes(value)
}

export interface WorkspaceSettingsDrawerProps {
  /** Whether the drawer is currently visible. */
  open: boolean
  /** Called when the user dismisses the drawer via close button, backdrop, or escape. */
  onClose: () => void
  /**
   * Which tab should be selected when the drawer opens. Defaults to
   * {@code 'general'} when omitted or when the value is unrecognised.
   */
  initialTab?: WorkspaceSettingsTab
}

/**
 * Right-side drawer that owns workspace-level settings (General, Members,
 * Integrations, Repositories). Replaces three previously-routed top-nav pages
 * with a single in-context overlay.
 *
 * <p>This skeleton renders the chrome — header with workspace name + close
 * affordance, a vertical left-rail Tabs nav with a bronze active-edge accent,
 * and a per-tab placeholder body. Parallel cards fill in each tab's real
 * content.</p>
 *
 * <p>Selected-tab state lives locally in this component. The {@code initialTab}
 * prop seeds the value on open; a later iteration may promote this to a URL
 * hash for deep-linking.</p>
 */
export function WorkspaceSettingsDrawer({
  open,
  onClose,
  initialTab = 'general',
}: WorkspaceSettingsDrawerProps) {
  const { currentWorkspace, currentSlug } = useWorkspace()

  const resolvedInitial: WorkspaceSettingsTab = isWorkspaceSettingsTab(initialTab)
    ? initialTab
    : 'general'

  const [selectedTab, setSelectedTab] = useState<WorkspaceSettingsTab>(resolvedInitial)

  // Reset the selected tab to the requested initial whenever the drawer is
  // (re-)opened. A closed drawer doesn't need to react to {@code initialTab}
  // changes, and tracking the {@code open} edge avoids stomping a user's
  // mid-session tab pick.
  useEffect(() => {
    if (open) {
      setSelectedTab(resolvedInitial)
    }
  }, [open, resolvedInitial])

  const workspaceName = currentWorkspace?.name ?? currentSlug ?? 'Workspace'

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
        aria-label="Workspace settings"
        sx={{
          height: '100%',
          display: 'flex',
          flexDirection: 'column',
          overflow: 'hidden',
        }}
      >
        {/* ── Header: workspace name + close ───────────────────────────── */}
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
              Settings
            </Typography>
            <Typography
              component="h2"
              sx={{
                fontSize: '0.9375rem',
                fontWeight: 500,
                letterSpacing: '-0.005em',
                color: tokens.textPrimary,
              }}
            >
              {workspaceName}
            </Typography>
          </Stack>
          <IconButton
            size="small"
            aria-label="Close settings"
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

        {/* ── Body: left rail + main content ────────────────────────────── */}
        <Box
          sx={{
            flex: 1,
            minHeight: 0,
            display: 'flex',
            flexDirection: 'row',
          }}
        >
          {/* Left rail — vertical Tabs nav with bronze active-edge accent */}
          <Box
            component="nav"
            aria-label="Workspace settings sections"
            sx={{
              width: 200,
              flexShrink: 0,
              backgroundColor: tokens.chromeBg,
              borderRight: `1px solid ${tokens.hairline}`,
              py: 1.5,
              overflowY: 'auto',
            }}
          >
            <Tabs
              orientation="vertical"
              value={selectedTab}
              onChange={(_e, value: WorkspaceSettingsTab) => setSelectedTab(value)}
              TabIndicatorProps={{ sx: { display: 'none' } }}
              sx={{
                minHeight: 0,
                '& .MuiTabs-flexContainer': {
                  gap: 0.25,
                },
              }}
            >
              {TAB_DESCRIPTORS.map((descriptor) => (
                <Tab
                  key={descriptor.value}
                  value={descriptor.value}
                  label={descriptor.label}
                  disableRipple
                  sx={{
                    alignItems: 'flex-start',
                    justifyContent: 'flex-start',
                    textAlign: 'left',
                    textTransform: 'none',
                    minHeight: 36,
                    minWidth: 0,
                    px: 2.5,
                    py: 0.75,
                    fontSize: '0.8125rem',
                    fontWeight: 400,
                    letterSpacing: '-0.005em',
                    color: tokens.textMuted,
                    borderLeft: '2px solid transparent',
                    borderRadius: 0,
                    transition: 'color 200ms ease, border-color 200ms ease, font-weight 200ms ease',
                    '&:hover': {
                      color: tokens.textPrimary,
                    },
                    '&.Mui-selected': {
                      color: tokens.textPrimary,
                      fontWeight: 500,
                      borderLeftColor: tokens.accent,
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

          {/* Main content — renders the active tab's panel */}
          <Box
            component="main"
            role="tabpanel"
            aria-label={`${selectedTab} settings`}
            sx={{
              flex: 1,
              minWidth: 0,
              overflowY: 'auto',
              p: { xs: 3, md: 4 },
              backgroundColor: tokens.canvasBg,
            }}
          >
            {selectedTab === 'general' && (
              <WorkspaceGeneralTab onDeleted={onClose} />
            )}
            {selectedTab === 'members' && <MembersTab />}
            {selectedTab === 'integrations' && <IntegrationsTab />}
            {selectedTab === 'repositories' && <RepositoriesTab />}
            {selectedTab === 'specs' && <SpecsTab />}
          </Box>
        </Box>
      </Box>
    </Drawer>
  )
}

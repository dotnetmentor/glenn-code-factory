/**
 * PlaygroundView — Phase 1 primitives demo surface.
 *
 * <p>Shows every workspace primitive in every state, on the current (mode,
 * accent) pair. The sidebar's theme/accent toggle (see Phase 0) drives the
 * flips — the playground itself just renders, so we can verify each primitive
 * reads correctly under any combination.
 *
 * <p>The page is intentionally chrome-light: a single column of titled
 * sections, generous breathing room, no MUI cards or paper — the workspace
 * canvas is the surface.
 */
import { useState } from 'react'
import { Box, Stack, Typography } from '@mui/material'
import VisibilityIcon from '@mui/icons-material/Visibility'
import DescriptionIcon from '@mui/icons-material/Description'
import ViewKanbanIcon from '@mui/icons-material/ViewKanban'
import HistoryIcon from '@mui/icons-material/History'
import {
  FlatSwitch,
  InlineCode,
  KbdChip,
  RuntimePill,
  SegmentedTabs,
  StatusDot,
  pageTitleSx,
  sectionTitleSx,
  bodySx,
  captionSx,
  labelSx,
  surfaceTokens,
  workspaceText,
  workspaceCanvasInset,
} from '../../../shared'

type PreviewTab = 'preview' | 'changes' | 'specs' | 'kanban'

/**
 * One titled section block — every primitive group renders inside one of these
 * so the page reads as a vertical rhythm rather than a wall of components.
 */
function Section({
  title,
  description,
  children,
}: {
  title: string
  description?: string
  children: React.ReactNode
}) {
  return (
    <Stack spacing={2}>
      <Box>
        <Typography sx={sectionTitleSx}>{title}</Typography>
        {description ? (
          <Typography sx={{ ...captionSx, mt: 0.5 }}>{description}</Typography>
        ) : null}
      </Box>
      <Box
        sx={{
          padding: 3,
          borderRadius: 2,
          backgroundColor: surfaceTokens.surface,
          border: `1px solid ${surfaceTokens.hairline}`,
        }}
      >
        {children}
      </Box>
    </Stack>
  )
}

/**
 * Tiny inline label used to caption each variant in a row of primitives.
 */
function Caption({ children }: { children: React.ReactNode }) {
  return (
    <Typography sx={{ ...labelSx, color: workspaceText.faint }}>
      {children}
    </Typography>
  )
}

export function PlaygroundView() {
  // Local controlled state for the interactive primitives.
  const [previewTab, setPreviewTab] = useState<PreviewTab>('preview')
  const [yoloEnabled, setYoloEnabled] = useState(true)
  const [autoApprove, setAutoApprove] = useState(false)
  const [strictMode, setStrictMode] = useState(true)

  return (
    <Box
      sx={{
        height: '100%',
        overflow: 'auto',
        backgroundColor: surfaceTokens.canvasBg,
        padding: { xs: 2, md: workspaceCanvasInset.desktopPadding * 2 },
      }}
    >
      <Box sx={{ maxWidth: 880, marginInline: 'auto' }}>
        <Stack spacing={4}>
          {/* Page header */}
          <Box>
            <Typography sx={pageTitleSx}>Workspace primitives</Typography>
            <Typography sx={{ ...bodySx, mt: 1 }}>
              Phase 1 building blocks, all states, on the current (mode, accent) pair.
              Use the sidebar footer to flip themes and cycle accents — every primitive
              here should repaint without a refresh.
            </Typography>
          </Box>

          {/* RuntimePill — all states */}
          <Section
            title="RuntimePill"
            description="Pill with status dot · label · sub-label. Transitional states pulse."
          >
            <Stack spacing={3}>
              <Stack direction="row" spacing={2} flexWrap="wrap" useFlexGap>
                <Stack spacing={1} alignItems="flex-start">
                  <Caption>Online</Caption>
                  <RuntimePill state="Online" subLabel="12d uptime" />
                </Stack>
                <Stack spacing={1} alignItems="flex-start">
                  <Caption>Booting</Caption>
                  <RuntimePill state="Booting" />
                </Stack>
                <Stack spacing={1} alignItems="flex-start">
                  <Caption>Bootstrapping</Caption>
                  <RuntimePill state="Bootstrapping" subLabel="mysql · 416 chars" />
                </Stack>
                <Stack spacing={1} alignItems="flex-start">
                  <Caption>Suspended</Caption>
                  <RuntimePill state="Suspended" />
                </Stack>
                <Stack spacing={1} alignItems="flex-start">
                  <Caption>Failed</Caption>
                  <RuntimePill state="Failed" />
                </Stack>
                <Stack spacing={1} alignItems="flex-start">
                  <Caption>Interactive</Caption>
                  <RuntimePill
                    state="Online"
                    subLabel="click me"
                    onClick={() => window.alert('Runtime pill clicked')}
                  />
                </Stack>
              </Stack>
            </Stack>
          </Section>

          {/* SegmentedTabs */}
          <Section
            title="SegmentedTabs"
            description="Pill-grouped tabs. Used in app-container bottom strip, drawer device toggle, approval matrix."
          >
            <Stack spacing={3}>
              <Stack spacing={1} alignItems="flex-start">
                <Caption>With icons</Caption>
                <SegmentedTabs<PreviewTab>
                  value={previewTab}
                  onChange={setPreviewTab}
                  items={[
                    { id: 'preview', label: 'Preview', icon: VisibilityIcon },
                    { id: 'changes', label: 'Changes', icon: DescriptionIcon, count: 3 },
                    { id: 'specs', label: 'Specs', icon: HistoryIcon },
                    { id: 'kanban', label: 'Kanban', icon: ViewKanbanIcon, count: 12 },
                  ]}
                />
              </Stack>
              <Stack spacing={1} alignItems="flex-start">
                <Caption>Label-only</Caption>
                <SegmentedTabs<PreviewTab>
                  value={previewTab}
                  onChange={setPreviewTab}
                  items={[
                    { id: 'preview', label: 'Desktop' },
                    { id: 'changes', label: 'Tablet' },
                    { id: 'specs', label: 'Mobile' },
                  ]}
                />
              </Stack>
              <Stack spacing={1} alignItems="flex-start" sx={{ width: '100%' }}>
                <Caption>Full width</Caption>
                <SegmentedTabs<PreviewTab>
                  value={previewTab}
                  onChange={setPreviewTab}
                  fullWidth
                  items={[
                    { id: 'preview', label: 'Preview', icon: VisibilityIcon },
                    { id: 'changes', label: 'Changes', count: 3 },
                    { id: 'kanban', label: 'Kanban', count: 12 },
                  ]}
                />
              </Stack>
            </Stack>
          </Section>

          {/* StatusDot */}
          <Section
            title="StatusDot"
            description="Bare semantic dot. Pulses on transitional states; supports a manual override."
          >
            <Stack direction="row" spacing={3} flexWrap="wrap" useFlexGap alignItems="center">
              <Stack spacing={1} alignItems="center">
                <Caption>Online</Caption>
                <StatusDot state="Online" />
              </Stack>
              <Stack spacing={1} alignItems="center">
                <Caption>Booting</Caption>
                <StatusDot state="Booting" />
              </Stack>
              <Stack spacing={1} alignItems="center">
                <Caption>Suspended</Caption>
                <StatusDot state="Suspended" />
              </Stack>
              <Stack spacing={1} alignItems="center">
                <Caption>Failed</Caption>
                <StatusDot state="Failed" />
              </Stack>
              <Stack spacing={1} alignItems="center">
                <Caption>Force pulse</Caption>
                <StatusDot state="Online" pulse />
              </Stack>
              <Stack spacing={1} alignItems="center">
                <Caption>Larger (size=12)</Caption>
                <StatusDot state="Online" size={12} />
              </Stack>
            </Stack>
          </Section>

          {/* FlatSwitch */}
          <Section
            title="FlatSwitch"
            description="Compact toggle. Flatter than MUI's default — used in Approvals + Runtime services."
          >
            <Stack direction="row" spacing={4} flexWrap="wrap" useFlexGap>
              <Stack spacing={1.5} alignItems="flex-start">
                <Caption>YOLO mode</Caption>
                <Stack direction="row" spacing={1.5} alignItems="center">
                  <FlatSwitch
                    checked={yoloEnabled}
                    onChange={setYoloEnabled}
                    ariaLabel="YOLO mode"
                  />
                  <Typography sx={captionSx}>
                    {yoloEnabled ? 'On — auto-approve every step' : 'Off — ask first'}
                  </Typography>
                </Stack>
              </Stack>
              <Stack spacing={1.5} alignItems="flex-start">
                <Caption>Auto-approve</Caption>
                <FlatSwitch
                  checked={autoApprove}
                  onChange={setAutoApprove}
                  ariaLabel="Auto approve"
                />
              </Stack>
              <Stack spacing={1.5} alignItems="flex-start">
                <Caption>Strict (disabled)</Caption>
                <FlatSwitch
                  checked={strictMode}
                  onChange={setStrictMode}
                  disabled
                  ariaLabel="Strict mode"
                />
              </Stack>
              <Stack spacing={1.5} alignItems="flex-start">
                <Caption>Wider (width=44)</Caption>
                <FlatSwitch
                  checked={yoloEnabled}
                  onChange={setYoloEnabled}
                  width={44}
                  ariaLabel="Wider switch"
                />
              </Stack>
            </Stack>
          </Section>

          {/* KbdChip */}
          <Section
            title="KbdChip"
            description="Tiny <kbd> chip for keyboard hints. Used in the composer hint row + command palette."
          >
            <Stack spacing={2}>
              <Stack direction="row" spacing={1} alignItems="center">
                <Typography sx={captionSx}>Send message</Typography>
                <KbdChip ariaLabel="Command">⌘</KbdChip>
                <KbdChip ariaLabel="Enter">↵</KbdChip>
              </Stack>
              <Stack direction="row" spacing={1} alignItems="center">
                <Typography sx={captionSx}>Search</Typography>
                <KbdChip>⌘</KbdChip>
                <KbdChip>K</KbdChip>
              </Stack>
              <Stack direction="row" spacing={1} alignItems="center">
                <Typography sx={captionSx}>Toggle sidebar</Typography>
                <KbdChip>⌘</KbdChip>
                <KbdChip>B</KbdChip>
              </Stack>
              <Stack direction="row" spacing={1} alignItems="center">
                <Typography sx={captionSx}>Escape modal</Typography>
                <KbdChip>Esc</KbdChip>
              </Stack>
            </Stack>
          </Section>

          {/* InlineCode */}
          <Section
            title="InlineCode"
            description="Workspace-themed <code> chip for filenames, hashes, slugs, env vars."
          >
            <Stack spacing={2}>
              <Typography sx={bodySx}>
                Read <InlineCode>package.json</InlineCode> before running{' '}
                <InlineCode>npm install</InlineCode> — it pins the bun version.
              </Typography>
              <Typography sx={bodySx}>
                Commit <InlineCode>a1b2c3d</InlineCode> introduced the regression; revert to{' '}
                <InlineCode emphasis>f9e8d7c</InlineCode> to fix.
              </Typography>
              <Typography sx={bodySx}>
                Set <InlineCode size="sm">OPENAI_API_KEY</InlineCode> in{' '}
                <InlineCode size="sm">.env.local</InlineCode> (small variant for dense prose).
              </Typography>
              <Stack direction="row" spacing={1} flexWrap="wrap" useFlexGap>
                <InlineCode>main</InlineCode>
                <InlineCode>feature/spec-editor</InlineCode>
                <InlineCode>chore/upgrade-deps</InlineCode>
                <InlineCode emphasis>release/2026-05</InlineCode>
              </Stack>
            </Stack>
          </Section>
        </Stack>
      </Box>
    </Box>
  )
}

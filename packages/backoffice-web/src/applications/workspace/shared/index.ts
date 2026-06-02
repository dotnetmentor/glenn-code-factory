/**
 * Workspace shared primitives — the design language extracted from
 * {@code project-workspace} so every routed page can read as one product.
 *
 * <p>Consumers should import from this barrel, not from the individual files,
 * so the surface stays stable as primitives are added.</p>
 */

// Design tokens — colours, typography presets, spacing rhythm, the near-black
// pill button sx fragment. Single source of truth for the warm-paper palette.
export {
  bodySx,
  captionSx,
  chromeTokens,
  labelSx,
  pageTitleSx,
  pageCardEmptySx,
  pageCardFlushSx,
  pageCardPaddedSx,
  pageCardSx,
  sectionTitleSx,
  semanticTokens,
  semanticWarningRgb,
  surfaceTokens,
  workspaceAccent,
  workspaceCanvasInset,
  workspaceColors,
  workspaceFontFamily,
  workspacePanelShellSx,
  workspaceRuntime,
  workspaceShadows,
  workspaceSidebarWidth,
  workspaceSpacing,
  workspaceText,
  workspaceTokens,
} from './designTokens'

// Page chrome primitives — the shell, the header, and the section block. The
// "less cruddy" replacements for stock Card / overline / h4 stacks.
export {
  WorkspacePageHeader,
  WorkspacePageShell,
  WorkspaceSection,
  type WorkspacePageHeaderProps,
  type WorkspacePageShellProps,
  type WorkspaceSectionProps,
} from './WorkspacePageShell'

// Friendly empty state — generous, calm, with the near-black pill CTA.
export { EmptyState, type EmptyStateProps } from './EmptyState'

// Detached-GitHub affordances — the quiet pill shown when a project's
// installation was disconnected, plus the dialog used to reconnect.
export {
  DetachedGithubPill,
  type DetachedGithubPillProps,
  type DetachedGithubPillProjectInfo,
} from './components/DetachedGithubPill'
export {
  ReconnectProjectsDialog,
  type ReconnectProjectsDialogProps,
} from './components/ReconnectProjectsDialog'

// Reauthorize banner — surfaced above the new-project form when the
// create-repo flow needs a fresh User Access Token (see
// {@code github-user-oauth-repo-creation} spec).
export {
  GithubReauthorizeBanner,
  type GithubReauthorizeBannerProps,
} from './components/GithubReauthorizeBanner'
export { ManageGitHubAccessHint } from './components/ManageGitHubAccessHint'
export { PoolEmptyErrorAlert } from './components/PoolEmptyErrorAlert'
export { buildGithubInstallationManageUrl } from './githubInstallationManageUrl'
export { isPoolEmptyError, SUBDOMAINS_ADMIN_PATH } from './poolEmptyError'

// Phase 1 primitives — small, reusable building blocks (RuntimePill,
// SegmentedTabs, StatusDot, FlatSwitch, KbdChip, InlineCode). Live
// previews on {@code /w/:slug/playground}.
export * from './primitives'

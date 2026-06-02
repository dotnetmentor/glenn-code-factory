import PeopleIcon from "@mui/icons-material/People";
import HistoryIcon from "@mui/icons-material/History";
import PaletteIcon from "@mui/icons-material/Palette";
import BugReportIcon from '@mui/icons-material/BugReport'
import MarkEmailReadOutlinedIcon from '@mui/icons-material/MarkEmailReadOutlined'
import TuneIcon from '@mui/icons-material/Tune'
import VpnKeyIcon from '@mui/icons-material/VpnKey'
import AddCircleOutlineIcon from '@mui/icons-material/AddCircleOutline'
import MemoryIcon from '@mui/icons-material/Memory'
import LayersIcon from '@mui/icons-material/Layers'
import LanguageIcon from '@mui/icons-material/Language'
import DescriptionOutlinedIcon from '@mui/icons-material/DescriptionOutlined'
import ViewKanbanOutlinedIcon from '@mui/icons-material/ViewKanbanOutlined'
import MonitorHeartIcon from '@mui/icons-material/MonitorHeart'
import SmartToyIcon from '@mui/icons-material/SmartToy'
import SpeedIcon from '@mui/icons-material/Speed'
import RocketLaunchIcon from '@mui/icons-material/RocketLaunch'
import DeleteSweepIcon from '@mui/icons-material/DeleteSweep'
import SettingsApplicationsIcon from '@mui/icons-material/SettingsApplications'
import BackupIcon from '@mui/icons-material/Backup'
import { RouteDefinition } from "../../app/routing/types";
import { UsersPage } from "./features/users/routes/UsersPage";
import { DomainEventsPage } from "./features/domain-events/routes/DomainEventsPage";
import { ThemePlaygroundPage } from "./features/theme-playground/routes/ThemePlaygroundPage";
import { ErrorLogsPage } from './features/errorlog/routes/ErrorLogsPage'
import { ErrorTestPage } from './features/errorlog/routes/ErrorTestPage'
import { WaitlistPage } from './features/waitlist/routes/WaitlistPage'
import { SystemSettingsPage } from './features/system-settings/routes/SystemSettingsPage'
import { EnvironmentBackupPage } from './features/environment-backup'
import { ProjectSettingsPage } from './features/project-secrets/routes/ProjectSettingsPage'
import { SubdomainsPage } from './features/cloudflare-subdomains'
import {
  OnboardingChooser,
  ManualStackPicker,
  AiOnboarding,
} from './features/project-onboarding'
import { RuntimeWorkspacePage } from './features/project-runtime'
import { RuntimeImagesPage } from './features/runtime-images/routes/RuntimeImagesPage'
import { SpecListPage, SpecDetailPage } from './features/specifications'
import { KanbanBoardPage } from './features/kanban'
import { RuntimeMonitorPage } from './features/runtime-monitor'
import { AgentModelsPage } from './features/agent-models/routes/AgentModelsPage'
import { RuntimeWakeObservabilityPage } from './features/runtime-wake-observability'
import { StartersPage } from './features/starters/routes/StartersPage'
import { FlyCleanupPage } from './features/fly-cleanup'
import { RuntimePresetsListPage } from './features/runtime-presets/routes/RuntimePresetsListPage'
import { RuntimePresetEditPage } from './features/runtime-presets/routes/RuntimePresetEditPage'

export const superAdminRoutes: RouteDefinition[] = [
  {
    path: "/super-admin",
    label: "Users",
    icon: PeopleIcon,
    component: UsersPage,
  },
  {
    path: "/super-admin/users",
    label: "Users",
    icon: PeopleIcon,
    component: UsersPage,
    hideInNavigation: true,
  },
  {
    path: "/super-admin/system-settings",
    label: "System Settings",
    icon: TuneIcon,
    component: SystemSettingsPage,
  },
  {
    path: '/super-admin/environment-backup',
    label: 'Environment Backup',
    icon: BackupIcon,
    component: EnvironmentBackupPage,
  },
  {
    path: "/super-admin/domain-events",
    label: "Domain Events",
    icon: HistoryIcon,
    component: DomainEventsPage,
    hideInNavigation: true,
  },
  {
    path: "/super-admin/theme-playground",
    label: "Theme Playground",
    icon: PaletteIcon,
    component: ThemePlaygroundPage,
    hideInNavigation: true,
  },
  {
    path: '/super-admin/error-logs',
    label: 'Error Log',
    icon: BugReportIcon,
    component: ErrorLogsPage,
    hideInNavigation: true,
  },
  {
    path: '/super-admin/waitlist',
    label: 'Waitlist',
    icon: MarkEmailReadOutlinedIcon,
    component: WaitlistPage,
  },
  // Dev-only E2E acceptance-test route. Guarded further inside the component
  // via `import.meta.env.DEV`, so even in a prod bundle it renders a stub.
  {
    path: '/super-admin/error-test',
    label: 'Error Test',
    icon: BugReportIcon,
    component: ErrorTestPage,
    hideInNavigation: true,
  },
  // Project onboarding — top-level CTA in the sidebar. The chooser is the
  // entry point; the two flow pages are hidden (deep-linked from the chooser).
  {
    path: '/super-admin/projects/new',
    label: 'New project',
    icon: AddCircleOutlineIcon,
    component: OnboardingChooser,
  },
  {
    path: '/super-admin/projects/new/manual',
    label: 'Pick my stack',
    icon: AddCircleOutlineIcon,
    component: ManualStackPicker,
    hideInNavigation: true,
  },
  {
    path: '/super-admin/projects/new/ai',
    label: 'Let the agent figure it out',
    icon: AddCircleOutlineIcon,
    component: AiOnboarding,
    hideInNavigation: true,
  },
  // Project settings — deep-linked from project pages, no top-nav entry.
  // Settings shell hosts tabs; the Environment tab is the only one for now.
  {
    path: '/super-admin/projects/:projectId/settings',
    label: 'Project Settings',
    icon: VpnKeyIcon,
    component: ProjectSettingsPage,
    hideInNavigation: true,
  },
  {
    path: '/super-admin/projects/:projectId/settings/environment',
    label: 'Environment Variables',
    icon: VpnKeyIcon,
    component: ProjectSettingsPage,
    hideInNavigation: true,
  },
  {
    path: '/super-admin/projects/:projectId/settings/credentials',
    label: 'Project Credentials',
    icon: VpnKeyIcon,
    component: ProjectSettingsPage,
    hideInNavigation: true,
  },
  // Project runtime workspace — landing page after AI onboarding (Spec 16
  // Card 8). Hosts the runtime status header, pending RuntimeProposalCards,
  // and the proposal history. Deep-linked from the onboarding flow.
  {
    path: '/super-admin/projects/:projectId/runtime',
    label: 'Runtime',
    icon: MemoryIcon,
    component: RuntimeWorkspacePage,
    hideInNavigation: true,
  },
  // Project specifications — read-only list of the markdown specs the agent
  // has drafted via the planning MCP. Surfaced in the sidebar whenever the
  // user is on a project URL (the `:projectId` is resolved from the current
  // pathname by `useResolveAppPath`). The detail route stays hidden — it's
  // the destination of a row click on the list, not a top-level entry.
  {
    path: '/super-admin/projects/:projectId/specs',
    label: 'Specs',
    icon: DescriptionOutlinedIcon,
    component: SpecListPage,
  },
  {
    path: '/super-admin/projects/:projectId/specs/:slug',
    label: 'Specification',
    icon: DescriptionOutlinedIcon,
    component: SpecDetailPage,
    hideInNavigation: true,
  },
  // Project kanban board — drag-and-drop view of the planning cards the
  // agent owns through the kanban MCP. Same `:projectId` resolution flow
  // as Specs above.
  {
    path: '/super-admin/projects/:projectId/kanban',
    label: 'Kanban',
    icon: ViewKanbanOutlinedIcon,
    component: KanbanBoardPage,
  },
  // Runtime base images — operational surface for picking which Fly registry
  // tag is active and used by the runtime provisioner.
  {
    path: '/super-admin/runtime-images',
    label: 'Runtime Images',
    icon: LayersIcon,
    component: RuntimeImagesPage,
  },
  // Agent model catalogue — the curated Anthropic model slugs that projects
  // (and per-session overrides) can pick from. Super-admin only.
  {
    path: '/super-admin/agent-models',
    label: 'Agent Models',
    icon: SmartToyIcon,
    component: AgentModelsPage,
  },
  // Starters (ProjectTemplates) catalogue — curated GitHub template repos
  // paired with an optional Runtime Spec, surfaced on the new-project picker.
  // Super-admin only.
  {
    path: '/super-admin/starters',
    label: 'Starters',
    icon: RocketLaunchIcon,
    component: StartersPage,
  },
  // Runtime presets catalogue — service templates (kind=node-vite, postgres,
  // etc.) the runtime spec editor and AI onboarding pick from. Built-ins are
  // clone-only; everything else is editable. Super-admin only.
  {
    path: '/super-admin/runtime-presets',
    label: 'Runtime Presets',
    icon: SettingsApplicationsIcon,
    component: RuntimePresetsListPage,
  },
  {
    path: '/super-admin/runtime-presets/new',
    label: 'New Runtime Preset',
    icon: SettingsApplicationsIcon,
    component: RuntimePresetEditPage,
    hideInNavigation: true,
  },
  {
    path: '/super-admin/runtime-presets/:id',
    label: 'Edit Runtime Preset',
    icon: SettingsApplicationsIcon,
    component: RuntimePresetEditPage,
    hideInNavigation: true,
  },
  // Cloudflare subdomain pool — preview tunnels minted in advance and assigned
  // to branches as they are created. Super-admin only.
  {
    path: '/super-admin/subdomains',
    label: 'Subdomains',
    icon: LanguageIcon,
    component: SubdomainsPage,
  },
  // Runtime drift monitor — operator view that compares DB-tracked runtime
  // state against Fly's actual machine state and flags drift severity.
  {
    path: '/super-admin/runtimes/monitor',
    label: 'Runtime Monitor',
    icon: MonitorHeartIcon,
    component: RuntimeMonitorPage,
  },
  // Runtime wake observability — fleet wake-performance triage. p50/p95
  // summary, per-stage breakdown, and a slow-sessions list that deep-links
  // into the existing RuntimeDrawer (Spec: runtime-wake-observability).
  {
    path: '/super-admin/runtime-wake-observability',
    label: 'Wake Observability',
    icon: SpeedIcon,
    component: RuntimeWakeObservabilityPage,
  },
  // Fly cleanup — destructive operator tool for purging orphaned Fly.io
  // machines and volumes after their owning runtimes are deleted. Two
  // tabs (machines | volumes) with bulk-destroy gated by a typed-DELETE
  // confirmation. Super-admin only.
  {
    path: '/super-admin/fly-cleanup',
    label: 'Fly Cleanup',
    icon: DeleteSweepIcon,
    component: FlyCleanupPage,
  },
];

// Daemon SignalR wire types.
//
// **Card 2 of the daemon-codegen migration.** This module used to be a
// hand-written ~18KB mirror of the .NET DTOs in
// `RuntimeServerPayloads.cs` / `RuntimeClientPayloads.cs`. That hand-roll was a
// constant source of drift — the .NET side moved, the TS side didn't, and the
// only thing that caught us was a runtime "binding error" deep in the SignalR
// invoker.
//
// We now mirror the frontend's pattern: every contract type comes from
// `dotnet tsrts` output under `../generated/signalr/`. This file is a thin
// **re-export shim** so the dozens of importers across the daemon keep
// working unchanged. New code should import directly from
// `../generated/signalr/...`.
//
// A handful of types still live here as **local-only** declarations:
//   - `SessionEvent` is the daemon's own typed envelope for `eventData`
//     payloads — no .NET counterpart, deliberately open.
//   - `TurnCompletedPayload` is shaped here for the daemon's outbound
//     `EmitEvent(eventType=TurnCompleted)` carrier; the .NET side stores it
//     as raw JSON in the `eventData` jsonb column, so there's no .NET DTO to
//     transpile from.
//   - `RuntimeSpecProposal`, `RuntimeSpecDelta`, `ServiceSpec` have no .NET
//     counterparts (yet) — they're forward-declared here for the runtime-
//     curation hub methods. When the .NET DTOs land they'll move into the
//     generated bundle and disappear from here.
//   - `BootstrapEnvVar` / `BootstrapHooksConfig` / `BootstrapMcpServer` /
//     `BootstrapRepoConfig` / `RuntimeSpecV1` are aliases over the
//     generated `EnvVar` / `HooksConfig` / `McpServer` / `RepoConfig` /
//     `RuntimeSpecV1` shapes — kept under their original names so existing
//     bootstrap stages don't have to be renamed.

// ============================================================================
// Re-exports from generated TypedSignalR contracts
// ============================================================================

export type {
  ErrorReportPayload,
  DiskPressurePayload,
  RestartServicePayload,
  ForceRebootstrapPayload,
} from '../generated/signalr/Source.Features.SignalR.Contracts.js'

/**
 * Daemon-friendly form of `AgentSecretsDto`. Generated tsrts emits the field
 * as TypeScript-optional (`?: string`) because the .NET DTO uses `string?`;
 * the wire reality is JSON `null` when a credential is not set. The daemon's
 * tests pass `null` explicitly, so we re-declare with a nullable field here.
 *
 * `cursorApiKey` is resolved by `RuntimeHub.GetSecrets()` through the project
 * envelope → SystemSettings → env-var fallback chain and consumed by
 * `CursorFactory`'s BYOK env handoff: the @cursor/sdk reads `CURSOR_API_KEY`
 * from the agent process's env.
 */
export type AgentSecretsDto = {
  cursorApiKey: string | null
}

// Loosened wire-compat variants. The .NET DTOs declare these fields as
// nullable (`int?` etc.) which `dotnet tsrts` projects as TypeScript optional
// (`field?: T`). The daemon historically writes explicit `null` into the wire
// to mean "no value", which is valid JSON — the generated `?` shape rejects
// that. We re-declare the shape with `T | null` where the wire genuinely
// carries `null`, and add forward-compat fields the daemon reads tolerantly
// (e.g. `hooksKillSwitch`) but the .NET DTO doesn't ship today.

import type {
  HeartbeatPayload as GeneratedHeartbeatPayload,
  EmitEventPayload as GeneratedEmitEventPayload,
  StartTurnPayload as GeneratedStartTurnPayload,
  CancelTurnPayload as GeneratedCancelTurnPayload,
  ConfigUpdatePayload as GeneratedConfigUpdatePayload,
} from '../generated/signalr/Source.Features.SignalR.Contracts.js'
import type { AgentEventKind as GeneratedAgentEventKind } from '../generated/signalr/Source.Features.Conversations.Models.js'

/**
 * Wire shape the daemon emits for `Heartbeat`. Nullable fields are explicit
 * `null` on the wire (the .NET DTO uses `int?`/`double?` which serialise as
 * JSON `null` when absent), so the daemon-friendly type lifts the optional
 * markers to nullable.
 */
export type HeartbeatPayload = Omit<
  GeneratedHeartbeatPayload,
  | 'cpuPercent'
  | 'memoryUsedMb'
  | 'diskUsedPct'
  | 'supervisedServicesUp'
  | 'activeSessionId'
  | 'emittedAt'
  | 'disk'
  | 'sysstatsSnapshotJson'
> & {
  emittedAt: string
  cpuPercent: number | null
  memoryUsedMb: number | null
  diskUsedPct: number | null
  supervisedServicesUp: string[] | null
  activeSessionId: string | null
  /**
   * runtime-observability-super-admin — disk snapshot at heartbeat-emit time.
   * Generated DTO is `disk?: DiskSamplePayload` (.NET nullable record-typed
   * property). Daemon emits explicit `null` on the wire when no sample is
   * available yet, so the shim re-types as nullable. `sampledAt` is the
   * daemon's ISO-8601 UTC clock string — generated type is `Date | string`,
   * we pin to `string` because that's what `Date.toISOString()` returns.
   */
  disk: {
    usedBytes: number
    totalBytes: number
    sampledAt: string
  } | null
  /**
   * runtime-observability-super-admin — JSON-serialised `SysstatsSnapshot`
   * from `ProcessStatsCollector.latest()`. Explicit `null` on the wire when
   * the collector hasn't sampled yet or serialization failed.
   */
  sysstatsSnapshotJson: string | null
}

/**
 * Wire shape the daemon emits for `EmitEvent`. Post-Cursor-native rewrite
 * (card cursor-native-chat-ux 3.5/9) — the daemon now emits cursor-native
 * directly with NO wire-boundary translator. The .NET DTO carries:
 *
 *   - `kind: AgentEventKind` — the Cursor-native discriminator
 *     (PromptReceived/AssistantText/Thinking/ToolUse/Status/Task) — REQUIRED.
 *   - first-class typed fields per kind (text, toolCallId, toolName,
 *     toolStatus, toolArgs, toolResult, toolArgsTruncated,
 *     toolResultTruncated, thinkingDurationMs, runStatus, statusMessage,
 *     taskId, taskTitle) — the daemon populates the subset matching `kind`
 *     and the hub stamps them onto the per-kind AgentEvent columns.
 *   - `runResult?: RunResultPayload` — populated on the terminal Status
 *     frame so the hub can materialise the per-turn RunResult row in one
 *     trip without a follow-up REST call.
 *
 * `eventData` is retained as the catch-all opaque JSON envelope for the
 * stored-domain-event audit trail; new code should prefer the typed fields.
 *
 * `sessionId` is the empty-string sentinel for runtime-scope events
 * (bootstrap progress, no session yet) — the .NET side's
 * `EmptyStringNullableGuidJsonConverter` coerces it to null, but the wire
 * literally carries the empty string.
 */
export type EmitEventPayload = Omit<GeneratedEmitEventPayload, 'sessionId' | 'emittedAt' | 'kind'> & {
  sessionId: string
  emittedAt: string
  kind: GeneratedAgentEventKind
}

/**
 * Inbound StartTurn from the hub. Generated DTO doesn't carry `runtimeId`
 * because the hub pins the connection to one runtime via group routing — we
 * keep an optional declaration so `SignalRClient.#registerInboundHandler`'s
 * runtime-id guard is well-typed when (and if) the .NET side starts stamping
 * the field.
 */
export type StartTurnPayload = GeneratedStartTurnPayload & {
  runtimeId?: string
}

export type CancelTurnPayload = GeneratedCancelTurnPayload & {
  runtimeId?: string
}

/**
 * Wire-compat env-var delta. Generated shape uses `value?: string` (the
 * .NET property is `string?`) but the daemon's wire convention is an
 * explicit `null` for the "delete this key" case. Re-declare with a
 * nullable value so callers can keep using `null` without casts.
 */
export type EnvVarDelta = {
  key: string
  value: string | null
}

/**
 * Inbound config refresh. The generated DTO doesn't ship `hooksKillSwitch`
 * (.NET model doesn't define it yet) but the daemon reads it tolerantly so
 * a future backend addition lands without a wire break. We also override
 * `envVarsDelta` to use the wire-accurate nullable variant above.
 */
export type ConfigUpdatePayload = Omit<GeneratedConfigUpdatePayload, 'envVarsDelta'> & {
  envVarsDelta?: EnvVarDelta[]
  hooksKillSwitch?: boolean
}

export type {
  TurnRefusedPayload,
} from '../generated/signalr/Source.Features.Conversations.Models.js'

export type {
  RuntimeSpecDeltaApplyResultPayload,
} from '../generated/signalr/Source.Features.RuntimeCuration.Models.js'

export type {
  RequestDestructiveGitOpPayload,
  RequestDestructiveGitOpResponse,
} from '../generated/signalr/Source.Features.GitOps.Models.js'

import type {
  ApplyRuntimeSpecDeltaPayload as GeneratedApplyRuntimeSpecDeltaPayload,
} from '../generated/signalr/Source.Features.RuntimeCuration.Models.js'
import type {
  RuntimeSpecDeltaV2 as GeneratedRuntimeSpecDeltaV2,
} from '../generated/signalr/Source.Features.RuntimeCuration.js'

/**
 * V2 delta shape pushed from backend → daemon as the body of
 * `ApplyRuntimeSpecDelta`. The daemon walks the buckets:
 *   - `newOrChangedServices`: register / restart each `ServiceSpec`
 *   - `removedServices`: log + skip (additive-only Phase-1 semantics)
 *   - `installChanged`/`installNew`: TODO Phase 2 InstallStage
 *   - `setupChanged`/`setupNew`: re-run via bash -c
 *
 * Re-exported here so daemon-side modules can import the wire-accurate type
 * without depending directly on the generated file path.
 */
export type RuntimeSpecDeltaV2 = GeneratedRuntimeSpecDeltaV2
import type {
  MergeBranchPayload as GeneratedMergeBranchPayload,
} from '../generated/signalr/Source.Features.GitOps.Models.js'

/**
 * Inbound `ApplyRuntimeSpecDelta` from the hub. Generated DTO doesn't ship
 * `runtimeId`; we keep an optional declaration so the runtime-id guard
 * type-checks.
 */
export type ApplyRuntimeSpecDeltaPayload = GeneratedApplyRuntimeSpecDeltaPayload & {
  runtimeId?: string
}

/**
 * Inbound `MergeBranch` from the hub. Same pattern as ApplyRuntimeSpecDelta:
 * runtimeId is implicit via group routing on the wire but the daemon's
 * runtime-id guard reads it tolerantly.
 */
export type MergeBranchPayload = GeneratedMergeBranchPayload & {
  runtimeId?: string
}

// `EnvVarDelta` is re-declared below with a wire-accurate nullable `value`.

// `AgentEventKind` (post-Cursor-native rewrite, card 3/9) is the discriminator
// the .NET hub reads off the wire — emitted as an `enum` from `dotnet tsrts`.
// We re-export both the type and the value so callers can write either
// `AgentEventKind.ToolUse` or annotate parameters as `AgentEventKind`.
// `AgentEventRunStatus` and `AgentEventToolStatus` ride along — the mapper
// reads them as the typed per-kind sub-discriminators.
export {
  AgentEventKind,
  AgentEventRunStatus,
  AgentEventToolStatus,
} from '../generated/signalr/Source.Features.Conversations.Models.js'

// The legacy `AgentEventType` enum (Claude-flavored vocabulary —
// `TurnCompleted`, `TurnFailed`, `CommitMade`, `SystemMessage`,
// `AssistantThinking`, `ToolCall`, `ToolResult`, `ToolError`) was removed in
// card 3.5/9. The wire now requires `kind: AgentEventKind` directly; legacy
// call sites that announced run-level state (bootstrap progress, git-op
// rejections, shutdown breadcrumb, hooks) now use
// `kind: AgentEventKind.Status` and embed their semantic type in eventData.

// Bootstrap shapes — re-exported under their existing daemon names. The
// `Bootstrap*` aliases preserve the call sites without forcing a sweeping
// rename. `hooks` / `repo` are `null` on the wire when absent (the .NET DTO
// uses `?` reference types serialised as JSON `null`); generated tsrts
// projects these as TypeScript-optional but the daemon's tests/code pass
// explicit `null`, which is the wire reality. Loosen here.
//
// Post-V2 cutover (P1 wiring card 32b0481b): the daemon consumes
// `BootstrapPayloadV2` carrying a freeform `RuntimeSpecV2` (install bash,
// services[], setup bash). The V1 shape stays defined here for reference
// but is no longer consumed by any stage — the legacy languages map is gone.
import type {
  BootstrapPayloadV1 as GeneratedBootstrapPayloadV1,
  BootstrapPayloadV2 as GeneratedBootstrapPayloadV2,
  EnvVar as GeneratedEnvVar,
  HooksConfig as GeneratedHooksConfig,
  McpServer as GeneratedMcpServer,
  RepoConfig as GeneratedRepoConfig,
  RuntimeSpecV1 as GeneratedRuntimeSpecV1,
  RuntimeSpecV2 as GeneratedRuntimeSpecV2,
  ServiceSpec as GeneratedServiceSpec,
  HealthcheckSpec as GeneratedHealthcheckSpec,
} from '../generated/signalr/Source.Features.RuntimeBootstrap.Contracts.js'

export type BootstrapPayloadV1 = Omit<GeneratedBootstrapPayloadV1, 'hooks' | 'repo'> & {
  hooks: GeneratedHooksConfig | null
  repo: GeneratedRepoConfig | null
}

/**
 * V2 bootstrap payload — the daemon's runtime shape post-V2 cutover. Carries
 * a freeform `RuntimeSpecV2` (install bash, services[], setup bash) instead
 * of V1's languages-dict + setup-commands. Same null/loosen treatment as V1
 * for `hooks` / `repo`: wire reality is explicit `null`, generated tsrts
 * projects as optional.
 */
export type BootstrapPayloadV2 = Omit<GeneratedBootstrapPayloadV2, 'hooks' | 'repo'> & {
  hooks: GeneratedHooksConfig | null
  repo: GeneratedRepoConfig | null
}
export type BootstrapEnvVar = GeneratedEnvVar
export type BootstrapHooksConfig = GeneratedHooksConfig
export type BootstrapMcpServer = GeneratedMcpServer
export type BootstrapRepoConfig = GeneratedRepoConfig
export type RuntimeSpecV1 = GeneratedRuntimeSpecV1
export type RuntimeSpecV2 = GeneratedRuntimeSpecV2
export type BootstrapServiceSpec = GeneratedServiceSpec
export type BootstrapHealthcheckSpec = GeneratedHealthcheckSpec

// ============================================================================
// Daemon-local types (no .NET counterpart yet)
// ============================================================================

/**
 * Open shape covering every event-type payload the daemon emits. The .NET side
 * stores `eventData` as raw JSON in jsonb — this matches reality. Individual
 * emitters (TurnRunner, hooks, …) keep their own narrower shapes; this is the
 * common umbrella.
 */
export type SessionEvent = {
  type: string
  [key: string]: unknown
}

/**
 * Daemon-side payload for a turn-completion event. There is no .NET DTO with
 * this shape — turn completion now flows through the standard
 * `EmitEvent(eventType=TurnCompleted)` carrier, with this object serialised
 * into `eventData`. The hub's session state machine reads `success` /
 * `reason` / `newClaudeSessionId` out of the JSON.
 *
 * Card 2 cleanup: previously the daemon also called a non-existent
 * `TurnCompleted` hub method directly — that path is gone, only the EmitEvent
 * route remains.
 */
export type TurnCompletedPayload = {
  runtimeId: string
  sessionId: string
  turnId: string
  success: boolean
  reason?: string
  /** Cursor SDK agent id captured during this turn — fed back as resume hint. */
  newAgentId?: string
  error?: string
}

/**
 * Forward-declared shapes for the runtime-curation hub methods. The matching
 * .NET DTOs land in a later spec; until then these stay here so the daemon's
 * `SignalRClient.proposeRuntimeSpec` / `applyRuntimeSpecDelta` wrappers have a
 * stable parameter type.
 *
 * NOTE: the V2 `ServiceSpec` used by the wire (bootstrap payload + delta) is
 * re-exported as `BootstrapServiceSpec` from the generated file. This loose
 * proposal type stays for the legacy proposal hub method; it is intentionally
 * narrower (proposal flow doesn't need every field) and not to be confused
 * with the full V2 spec entry.
 */
export type ServiceSpec = {
  name: string
  command?: string
  autorestart?: boolean
}

export type RuntimeSpecProposal = {
  runtimeId: string
  sessionId: string
  turnId: string
  proposal: {
    languages?: string[]
    services?: ServiceSpec[]
    envVars?: Record<string, string>
    reason: string
  }
}

export type RuntimeSpecDelta = {
  runtimeId: string
  [key: string]: unknown
}

/**
 * Pre-codegen shape kept for the legacy importer in `RuntimeSpecApplier`.
 * The daemon's runtime-spec apply flow now uses
 * `ApplyRuntimeSpecDeltaPayload` from the generated bundle; this looser
 * type stays around until that file is fully migrated.
 */
export type RuntimeSpecDeltaPayload = {
  runtimeId: string
  version: string
  [key: string]: unknown
}

/**
 * Daemon-to-server execute notice for an approved destructive git op.
 * Mirrors the inbound `ExecuteDestructiveGitOp(opId)` server-to-daemon push.
 */
export type ExecuteDestructiveGitOpPayload = {
  runtimeId?: string
  opId: string
}

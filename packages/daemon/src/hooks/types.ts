// Wire-format payloads for the daemon → main API hook events on the
// `RuntimeHub`. These mirror the .NET records in
//   /workspace/packages/dotnet-api/Source/Features/Hooks/Models/HookHubPayloads.cs
//
// === JSON casing ===
// SignalR's JSON protocol on the server is configured via
//   ServicesExtensions.AddRealTimeServices()
//     services.AddSignalR().AddJsonProtocol(opts => …)
// The default `JsonSerializerOptions(JsonSerializerDefaults.Web)` produces
// camelCase property names, so wire-side fields are camelCase here even though
// the C# records use PascalCase.
//
// === Enum encoding ===
// The .NET serializer registers `new JsonStringEnumConverter()` (no naming
// policy). With no naming policy, that converter writes the C# enum *member
// name* verbatim — i.e. PascalCase — so:
//   HookPoint.BeforePrompt   → "BeforePrompt"
//   HookFeedbackMode.OnFailure → "OnFailure"
// The .NET column conversion to int (`HasConversion<int>()` on the entity) is
// a database concern only; it does NOT affect the SignalR wire shape.
// Evidence:
//   /workspace/packages/dotnet-api/Source/Infrastructure/Extensions/ServicesExtensions.cs:71-72
//     options.PayloadSerializerOptions.Converters.Add(
//         new System.Text.Json.Serialization.JsonStringEnumConverter());

/**
 * Wire shape of `Source.Features.Hooks.Models.HookPoint` — string member name.
 */
export type HookPointWire = 'BeforePrompt' | 'AfterPrompt' | 'OnFileChange' | 'BeforeCommit'

/**
 * Wire shape of `Source.Features.Hooks.Models.HookFeedbackMode` — string member name.
 */
export type HookFeedbackModeWire = 'OnFailure' | 'Always' | 'Silent'

/**
 * Daemon-to-server: a hook process has begun. Mirrors `HookStartedPayload`
 * in the .NET project.
 */
export interface HookStartedPayload {
  executionId: string
  runtimeId: string
  conversationId: string | null
  turnId: string | null
  hookPoint: HookPointWire
  hookName: string
  cmd: string
  feedbackMode: HookFeedbackModeWire
  /** ISO 8601 UTC. */
  startedAt: string
}

/**
 * Daemon-to-server: a single newline-terminated stdout line streamed live
 * while the hook process is running. Mirrors `HookProgressPayload`.
 */
export interface HookProgressPayload {
  executionId: string
  runtimeId: string
  stdoutLine: string
  lineIndex: number
}

/**
 * Daemon-to-server: the hook process exited normally. Mirrors `HookCompletedPayload`.
 *
 * NOTE: `outputTail` must be ≤ 16 KiB (the .NET column is `nvarchar(16384)`).
 * `HookEventEmitter` enforces this via byte-aware truncation before emit.
 */
export interface HookCompletedPayload {
  executionId: string
  runtimeId: string
  exitCode: number
  durationMs: number
  outputTail: string
  outputHash: string
  timedOut: boolean
  /** ISO 8601 UTC. */
  endedAt: string
}

/**
 * Daemon-to-server: the hook could not run at all. Mirrors `HookConfigErrorPayload`.
 *
 * Same 16 KiB cap on `outputTail` as `HookCompletedPayload`.
 */
export interface HookConfigErrorPayload {
  executionId: string
  runtimeId: string
  reason: string
  outputTail: string
  /** ISO 8601 UTC. */
  endedAt: string
}

/**
 * Daemon-to-server: relay-only signal that a self-heal turn is starting.
 * Mirrors `HookSelfHealStartedPayload`.
 */
export interface HookSelfHealStartedPayload {
  runtimeId: string
  conversationId: string
  previousTurnId: string
  newTurnId: string
  iteration: number
}

/**
 * Daemon-to-server: relay-only signal that the self-heal budget is exhausted.
 * Mirrors `HookSelfHealMaxedOutPayload`.
 */
export interface HookSelfHealMaxedOutPayload {
  runtimeId: string
  conversationId: string
  turnId: string
  iteration: number
}

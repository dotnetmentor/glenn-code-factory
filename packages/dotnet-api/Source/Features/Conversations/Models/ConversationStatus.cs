using Tapper;

namespace Source.Features.Conversations.Models;

/// <summary>
/// Lifecycle of a <see cref="Conversation"/>.
///
/// <para>Persisted as a string in Postgres (see <c>ApplicationDbContext</c>) so
/// adding new states later does not break existing rows. The default global
/// query filter on <c>Conversation</c> hides <see cref="Archived"/> rows; admin
/// queries pass <c>IgnoreQueryFilters()</c> to see everything.</para>
///
/// <para><see cref="TranspilationSourceAttribute"/> exposes the enum to the
/// TypeScript generator so the SignalR / API contracts that ship status across
/// the wire produce a clean TS union on the frontend.</para>
/// </summary>
[TranspilationSource]
public enum ConversationStatus
{
    /// <summary>Default state — visible in the UI, accepts new sessions.</summary>
    Active = 0,

    /// <summary>User archived the conversation. Hidden from default queries; data retained.</summary>
    Archived = 1,
}

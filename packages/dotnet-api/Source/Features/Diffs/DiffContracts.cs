using Tapper;

namespace Source.Features.Diffs;

/// <summary>
/// Request payload sent server→daemon to ask for the list of files that
/// differ in the requested scope. Phase 1 of the diff-view-tab spec only
/// supports <c>workingTree</c> — branch / commit / range scopes are wired
/// in Phase 3 alongside the picker UI. <see cref="Base"/> and <see cref="Head"/>
/// are reserved for those future scopes.
///
/// <para>Field naming is camelCase on the wire (the daemon's hub binder
/// matches camelCase against PascalCase records) — see daemon
/// <c>DiffQueries.ts</c> for the consumer.</para>
/// </summary>
[TranspilationSource]
public sealed record ChangedFilesRequest(
    string Scope,
    string? Base,
    string? Head);

/// <summary>
/// Response shape for <c>GET /api/runtimes/{id}/diff/changed-files</c>. The
/// per-file detail (path, status, +/-) lives in <see cref="Files"/>; aggregate
/// counts are duplicated at the top so the chrome's stats badge doesn't need
/// to traverse the array. <see cref="Reason"/> is set when the daemon clipped
/// the array (currently only <c>"too-many"</c>); null on the happy path.
/// </summary>
[TranspilationSource]
public sealed record ChangedFilesResponse(
    string Scope,
    string? Base,
    string? Head,
    int TotalAdditions,
    int TotalDeletions,
    IReadOnlyList<ChangedFile> Files,
    string? Reason);

/// <summary>
/// One row in the file list. <see cref="Path"/> is the post-rename (head) path;
/// <see cref="OldPath"/> is populated only when <see cref="Status"/> is
/// <c>"renamed"</c>. Status vocabulary is the small fixed set the spec
/// documents — <c>added | modified | deleted | renamed | untracked | binary-modified</c>.
/// </summary>
[TranspilationSource]
public sealed record ChangedFile(
    string Path,
    string? OldPath,
    string Status,
    int Additions,
    int Deletions,
    bool IsBinary,
    long? SizeBytes);

/// <summary>
/// Request payload sent server→daemon for a single file's unified diff.
/// </summary>
[TranspilationSource]
public sealed record FileDiffRequest(
    string Scope,
    string? Base,
    string? Head,
    string Path);

/// <summary>
/// Response shape for <c>GET /api/runtimes/{id}/diff/file</c>. Deliberately
/// minimal — we ship unified-diff text and let the frontend renderer split
/// it into hunks / lines. <see cref="UnifiedDiff"/> is null when
/// <see cref="IsBinary"/> is true. <see cref="IsTruncated"/> means the diff
/// body exceeded the per-file 500 KB cap and the head slice is what's in
/// <see cref="UnifiedDiff"/>; the frontend prepends a calm warning row.
/// <see cref="Reason"/> values: <c>"binary"|"too-large"|"submodule"|null</c>.
/// </summary>
[TranspilationSource]
public sealed record FileDiffResponse(
    string Path,
    string Status,
    bool IsBinary,
    bool IsTruncated,
    string? UnifiedDiff,
    string? Reason);

/// <summary>
/// One commit on the branch-picker row. Returned newest-first from
/// <c>GET /api/runtimes/{id}/diff/commits</c>. Drives the Phase 3
/// commit-picker that lets the user pick any commit on the branch as the
/// diff base. <see cref="AuthorDate"/> round-trips as ISO-8601 with TZ
/// offset (e.g. <c>2026-05-13T10:42:11+02:00</c>) so the frontend can
/// format it in the user's locale without a second parse pass.
/// </summary>
[TranspilationSource]
public sealed record CommitInfo(
    string Sha,
    string Message,
    DateTimeOffset AuthorDate,
    string AuthorName);

/// <summary>
/// Response shape for <c>GET /api/runtimes/{id}/diff/commits</c>. Wrapping the
/// list in a record (rather than returning the bare array) leaves room to add
/// pagination / truncation hints later without breaking the wire contract,
/// matching the convention <see cref="ChangedFilesResponse"/> already uses.
/// </summary>
[TranspilationSource]
public sealed record CommitRangeResponse(
    IReadOnlyList<CommitInfo> Commits);

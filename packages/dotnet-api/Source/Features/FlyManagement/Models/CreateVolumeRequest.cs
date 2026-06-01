using System.Text.Json.Serialization;

namespace Source.Features.FlyManagement.Models;

/// <summary>
/// Body of <c>POST /v1/apps/{app}/volumes</c>. Volumes are persistent block storage
/// that machines can mount — every runtime gets its own volume mounted at <c>/data</c>
/// so customer state survives machine recreation.
///
/// <para><b>Encryption is on by default and intentional.</b> Fly's API treats
/// <c>encrypted</c> as opt-in, but we never want a runtime volume that holds customer
/// data to land unencrypted because someone forgot to pass the flag. The default sits
/// here — at the request type — rather than inside <c>FlyClient.CreateVolumeAsync</c>
/// so it survives any future caller that bypasses the constructor's named arguments.
/// The dedicated <c>CreateVolumeAsync_AlwaysSendsEncryptedTrue</c> test pins this
/// behaviour as a security regression guard.</para>
///
/// <para>Snake-case wire shape (<c>size_gb</c>) is handled by the FlyClient's shared
/// <c>JsonNamingPolicy.SnakeCaseLower</c> serialiser settings — no per-property
/// annotations required.</para>
///
/// <para><b><see cref="SizeGb"/> is nullable and omitted-when-null on the wire.</b>
/// Fresh-create requests (<c>FlyClient.CreateVolumeAsync</c>) MUST carry a size — Fly
/// has no default and rejects the call without it. Fork requests
/// (<c>FlyClient.ForkVolumeAsync</c>) MUST NOT carry a size: as of 2026 Fly's API
/// explicitly rejects <c>size_gb</c> on volume forks ("setting size_gb for volume
/// forks is not currently supported", HTTP 400) — even when the value matches the
/// source volume's size. The fork inherits the source's size automatically. Modeling
/// the field as <c>int?</c> + <c>WhenWritingNull</c> lets one DTO serve both shapes:
/// creates pass the int, forks pass <c>null</c> and the property disappears from
/// the JSON entirely.</para>
///
/// <para><b><see cref="SourceVolumeId"/> drives the fork path.</b> When non-null Fly
/// performs a live clone of the named volume instead of provisioning empty storage —
/// the field name and semantics ("fork from remote volume") come straight from Fly's
/// OpenAPI for <c>CreateVolumeRequest</c>. We keep it nullable so the same record
/// covers both fresh-create and fork; callers that go through
/// <c>FlyClient.ForkVolumeAsync</c> set it, everyone else leaves it null and gets
/// the legacy empty-volume behaviour. <see cref="RequireUniqueZone"/> tells Fly to
/// place the new volume on a different physical host from the source so a single
/// hardware failure can't take down both the original and its copy — defaults to
/// <c>true</c> for forks per Fly's recommendation, irrelevant for fresh creates
/// (omitting it via <c>null</c> lets Fly pick its own default).</para>
/// </summary>
public record CreateVolumeRequest(
    string Name,
    string Region,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? SizeGb,
    bool Encrypted = true,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? SourceVolumeId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    bool? RequireUniqueZone = null);

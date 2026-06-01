namespace Source.Features.RuntimeBootstrap.Contracts;

/// <summary>
/// Versioning constants for the bootstrap payload protocol. The daemon declares its
/// supported versions on connect; main API picks the highest mutual version. Bumping
/// the wire format means adding a new version here, leaving the old records in place
/// so older daemons keep working until they're rotated.
/// </summary>
public static class BootstrapPayloadVersions
{
    /// <summary>The original wire format with hardcoded language/service catalogs.</summary>
    public const string V1 = "v1";

    /// <summary>
    /// V2 of the bootstrap payload — carries a freeform <see cref="RuntimeSpecV2"/>
    /// (install / services[] / setup) instead of a fixed languages-dict +
    /// setup-commands shape. <see cref="BootstrapPayloadV2"/> is the wire record;
    /// <see cref="Queries.GetBootstrapQuery"/> emits this version as of the
    /// P1 wiring card (32b0481b).
    /// </summary>
    public const string V2 = "v2";

    /// <summary>Latest version main API knows how to emit. Today: v2.</summary>
    public const string Latest = V2;

    /// <summary>
    /// Versions main API can serialise. New daemons advertise their max supported version
    /// during the SignalR handshake; we pick the highest entry from <see cref="Supported"/>
    /// that the daemon also accepts. V1 is retired post-cutover because the
    /// daemon has nothing left to feed a V1 spec into (the legacy shim is gone).
    /// </summary>
    public static readonly IReadOnlyList<string> Supported = new[] { V2 };
}

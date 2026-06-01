namespace Source.Features.RuntimeImages.Services;

/// <summary>
/// Thrown by <see cref="FlyRegistryClient"/> when an OCI v2 call to the Fly registry
/// can't be completed cleanly. We surface a small, classified error code (<see cref="Kind"/>)
/// so the controller can map to a sensible HTTP status (502 for transport / upstream
/// auth failures, 404 for image-not-found) without sniffing exception messages.
/// </summary>
public sealed class FlyRegistryException : Exception
{
    public FlyRegistryException(FlyRegistryErrorKind kind, string message, Exception? inner = null)
        : base(message, inner)
    {
        Kind = kind;
    }

    public FlyRegistryErrorKind Kind { get; }
}

/// <summary>Coarse classification of registry call failures.</summary>
public enum FlyRegistryErrorKind
{
    /// <summary>HTTP transport-level failure (DNS, TCP, TLS, timeout).</summary>
    Transport,

    /// <summary>Upstream answered 401/403 — token missing, expired, or wrong scope.</summary>
    Unauthorized,

    /// <summary>Upstream answered 404 — image or tag does not exist on the registry.</summary>
    NotFound,

    /// <summary>Upstream answered with a non-2xx other than 401/403/404.</summary>
    Upstream,

    /// <summary>Response body did not parse as the expected OCI v2 shape.</summary>
    Protocol,
}

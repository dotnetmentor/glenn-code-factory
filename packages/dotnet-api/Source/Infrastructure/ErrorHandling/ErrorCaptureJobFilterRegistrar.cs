using Hangfire;

namespace Source.Infrastructure.ErrorHandling;

/// <summary>
/// Installs the singleton <see cref="ErrorCaptureJobFilter"/> into Hangfire's static
/// <see cref="GlobalJobFilters.Filters"/> collection on host startup.
///
/// <para>Runs regardless of whether the Hangfire <i>server</i> is enabled: the filter
/// instance is cheap, and it guarantees that any job subsequently enqueued (including
/// by tests that construct their own server later) is observed by the error pipeline.
/// Without a central install point, a misconfigured environment could silently lose
/// Hangfire failure capture — exactly the failure mode this pipeline exists to prevent.</para>
///
/// <para>Idempotent: if the filter is already installed (e.g. after a host reload) we
/// leave the existing registration in place rather than stacking duplicates.</para>
/// </summary>
public sealed class ErrorCaptureJobFilterRegistrar : IHostedService
{
    private readonly ErrorCaptureJobFilter _filter;

    public ErrorCaptureJobFilterRegistrar(ErrorCaptureJobFilter filter)
    {
        _filter = filter;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var alreadyRegistered = GlobalJobFilters.Filters
            .Select(f => f.Instance)
            .OfType<ErrorCaptureJobFilter>()
            .Any();

        if (!alreadyRegistered)
        {
            GlobalJobFilters.Filters.Add(_filter);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

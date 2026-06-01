using Api.Tests.Infrastructure;
using Hangfire;
using Hangfire.Storage;
using Source.Features.RuntimeLifecycle.Jobs;

namespace Api.Tests.Features.RuntimeLifecycle;

/// <summary>
/// Verifies that <see cref="RuntimeProvisionerJobRegistration"/> (invoked from
/// <c>HangfireStartupService</c>) registers the every-minute provisioner job under
/// id "runtime-provisioner" with the expected cron expression. Mirrors
/// <c>ErrorLogRetentionJobRegistrationTests</c>.
/// </summary>
[Collection(HangfireTestCollection.Name)]
public class RuntimeProvisionerJobRegistrationTests : HangfireTestBase
{
    [Fact]
    public void RecurringJob_RuntimeProvisioner_IsRegisteredWithCorrectCron()
    {
        var recurringJobManager = new RecurringJobManager(Storage);

        RuntimeProvisionerJobRegistration.Register(recurringJobManager);

        using var connection = Storage.GetConnection();
        var jobs = connection.GetRecurringJobs();

        var provisioner = jobs.FirstOrDefault(j => j.Id == "runtime-provisioner");
        provisioner.Should().NotBeNull("the provisioner should be registered under id 'runtime-provisioner'");
        provisioner!.Cron.Should().Be(Cron.Minutely(),
            "the provisioner must run every minute to drain the Pending queue promptly");
    }
}

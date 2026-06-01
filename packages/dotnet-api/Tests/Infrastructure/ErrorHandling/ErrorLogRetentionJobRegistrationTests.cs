using Hangfire;
using Hangfire.Storage;
using Source.Infrastructure.ErrorHandling;
using Source.Infrastructure.Services;

namespace Api.Tests.Infrastructure.ErrorHandling;

/// <summary>
/// Verifies that <see cref="ErrorLogRetentionJobRegistration"/> (invoked from
/// <see cref="HangfireStartupService"/>) registers the daily 03:00 retention job
/// under id "error-log-retention" with the expected cron expression.
/// </summary>
[Collection(HangfireTestCollection.Name)]
public class ErrorLogRetentionJobRegistrationTests : HangfireTestBase
{
    [Fact]
    public void RecurringJob_ErrorLogRetention_IsRegisteredWithCorrectCron()
    {
        var recurringJobManager = new RecurringJobManager(Storage);

        ErrorLogRetentionJobRegistration.Register(recurringJobManager);

        using var connection = Storage.GetConnection();
        var jobs = connection.GetRecurringJobs();

        var retentionJob = jobs.FirstOrDefault(j => j.Id == "error-log-retention");
        retentionJob.Should().NotBeNull("the retention job should be registered under id 'error-log-retention'");
        retentionJob!.Cron.Should().Be("0 3 * * *", "the job must run daily at 03:00 UTC");
    }
}

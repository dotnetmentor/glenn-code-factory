using Api.Tests.Infrastructure;
using Source.Features.ProjectSecrets.Models;
using Source.Features.ProjectSecrets.Queries;
using Source.Features.RuntimeBootstrap.Contracts;
using Source.Features.RuntimeCuration.Models;
using Source.Features.RuntimeCuration.Services;
using Source.Infrastructure;

namespace Api.Tests.Features.ProjectSecrets;

/// <summary>
/// Coverage for <see cref="GetBranchEnvStatusQueryHandler"/> — the daemon-independent
/// "what env vars does this branch still need" computation (scenario S1 of the
/// runtime-env-vars spec). Exercises the three intertwined pieces that only
/// compose at the query layer and so weren't reachable by the per-handler tests:
///
/// <list type="bullet">
///   <item><b>required</b> is the union of <see cref="ServiceSpec.RequiredEnv"/>
///         across the branch's CURRENT expanded spec (resolved through
///         <see cref="ICurrentExpandedSpecResolver"/> from the most-recent terminal
///         proposal), deduped by key with the first declaring service winning.</item>
///   <item><b>present</b> is the branch-effective key set — project-wide
///         (<c>BranchId == null</c>) rows unioned with this branch's rows, while a
///         different branch's rows are excluded.</item>
///   <item><b>missing</b> = required keys absent from present, and each required
///         item carries the correct per-key <c>Satisfied</c> flag.</item>
/// </list>
/// </summary>
public class GetBranchEnvStatusQueryHandlerTests
{
    private readonly string _dbName = Guid.NewGuid().ToString();

    private static string BuildExpandedSpec()
    {
        // Two services. OPENROUTER_API_KEY is declared by BOTH — the handler must
        // dedupe it to a single required item attributed to the FIRST declaring
        // service ("api"). QUEUE_URL is declared only by the worker and is the one
        // we leave unset so it surfaces as missing.
        var spec = new RuntimeSpecV2
        {
            Services = new List<ServiceSpec>
            {
                new()
                {
                    Name = "api",
                    Command = "dotnet run",
                    RequiredEnv = new List<RequiredEnvVar>
                    {
                        new() { Key = "OPENROUTER_API_KEY", Description = "LLM gateway key", Secret = true },
                        new() { Key = "API_BASE_URL", Secret = false },
                    },
                },
                new()
                {
                    Name = "worker",
                    Command = "dotnet run --worker",
                    RequiredEnv = new List<RequiredEnvVar>
                    {
                        new() { Key = "OPENROUTER_API_KEY", Description = "duplicate — should be deduped", Secret = true },
                        new() { Key = "QUEUE_URL" },
                    },
                },
            },
        };
        return spec.ToJson();
    }

    private static ProjectSecret Secret(Guid projectId, Guid? branchId, string key) => new()
    {
        Id = Guid.NewGuid(),
        ProjectId = projectId,
        BranchId = branchId,
        Key = key,
        Ciphertext = Array.Empty<byte>(),
        Nonce = Array.Empty<byte>(),
    };

    [Fact]
    public async Task Computes_branch_effective_present_required_dedup_and_missing()
    {
        var projectId = Guid.NewGuid();
        var branchId = Guid.NewGuid();
        var otherBranchId = Guid.NewGuid();

        await using (var seed = TestDbContextFactory.Create(_dbName))
        {
            // The deployed spec: most-recent terminal (Approved) proposal carries
            // the expanded V2 with requiredEnv.
            seed.RuntimeProposals.Add(new RuntimeProposal
            {
                Id = Guid.NewGuid(),
                ProjectId = projectId,
                RuntimeId = Guid.NewGuid(),
                Status = RuntimeProposalStatus.Approved,
                ProposedSpec = "{}",
                ExpandedSpec = BuildExpandedSpec(),
                DecidedAt = DateTime.UtcNow.AddMinutes(-5),
            });

            // A NEWER but still-Pending proposal that declares a bogus required
            // var. The resolver only honours terminal-write proposals, so this
            // must NOT leak into the required set — proving "required matches
            // what's actually deployed", not what's merely proposed.
            seed.RuntimeProposals.Add(new RuntimeProposal
            {
                Id = Guid.NewGuid(),
                ProjectId = projectId,
                RuntimeId = Guid.NewGuid(),
                Status = RuntimeProposalStatus.Pending,
                ProposedSpec = "{}",
                ExpandedSpec = new RuntimeSpecV2
                {
                    Services = new List<ServiceSpec>
                    {
                        new()
                        {
                            Name = "api",
                            Command = "dotnet run",
                            RequiredEnv = new List<RequiredEnvVar> { new() { Key = "BOGUS_PENDING_KEY" } },
                        },
                    },
                }.ToJson(),
            });

            // Branch-effective present set:
            //  - OPENROUTER_API_KEY on THIS branch              → satisfied
            //  - API_BASE_URL project-wide (null branch)        → satisfied
            //  - EXTRA_CONFIG project-wide, not required        → present but not required
            //  - WRONG_BRANCH_KEY on a DIFFERENT branch         → excluded from present
            seed.ProjectSecrets.Add(Secret(projectId, branchId, "OPENROUTER_API_KEY"));
            seed.ProjectSecrets.Add(Secret(projectId, null, "API_BASE_URL"));
            seed.ProjectSecrets.Add(Secret(projectId, null, "EXTRA_CONFIG"));
            seed.ProjectSecrets.Add(Secret(projectId, otherBranchId, "WRONG_BRANCH_KEY"));

            await seed.SaveChangesAsync();
        }

        await using var ctx = TestDbContextFactory.Create(_dbName);
        var handler = new GetBranchEnvStatusQueryHandler(ctx, new CurrentExpandedSpecResolver(ctx));

        var result = await handler.Handle(
            new GetBranchEnvStatusQuery(projectId, branchId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var status = result.Value;

        // present: branch-effective, sorted, excludes the other branch's key.
        status.Present.Should().Equal("API_BASE_URL", "EXTRA_CONFIG", "OPENROUTER_API_KEY");

        // required: deduped to 3 distinct keys (OPENROUTER_API_KEY collapsed),
        // sorted by key, BOGUS_PENDING_KEY absent (Pending proposal ignored).
        status.Required.Select(r => r.Key)
            .Should().Equal("API_BASE_URL", "OPENROUTER_API_KEY", "QUEUE_URL");

        var openrouter = status.Required.Single(r => r.Key == "OPENROUTER_API_KEY");
        openrouter.Service.Should().Be("api", "first declaring service wins on dedupe");
        openrouter.Secret.Should().BeTrue();
        openrouter.Description.Should().Be("LLM gateway key");
        openrouter.Satisfied.Should().BeTrue();

        status.Required.Single(r => r.Key == "API_BASE_URL").Satisfied.Should().BeTrue();
        status.Required.Single(r => r.Key == "QUEUE_URL").Satisfied.Should().BeFalse();

        // missing: just the one unsatisfied required key.
        status.Missing.Should().Equal("QUEUE_URL");
    }

    [Fact]
    public async Task No_terminal_proposal_yields_empty_required_and_missing()
    {
        var projectId = Guid.NewGuid();
        var branchId = Guid.NewGuid();

        await using (var seed = TestDbContextFactory.Create(_dbName))
        {
            // A stored var exists, but there is NO terminal proposal — a fresh
            // project. Present reflects the store; required/missing are empty
            // because nothing has declared a requirement yet.
            seed.ProjectSecrets.Add(Secret(projectId, branchId, "SOME_KEY"));
            await seed.SaveChangesAsync();
        }

        await using var ctx = TestDbContextFactory.Create(_dbName);
        var handler = new GetBranchEnvStatusQueryHandler(ctx, new CurrentExpandedSpecResolver(ctx));

        var result = await handler.Handle(
            new GetBranchEnvStatusQuery(projectId, branchId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Present.Should().Equal("SOME_KEY");
        result.Value.Required.Should().BeEmpty();
        result.Value.Missing.Should().BeEmpty();
    }
}

using MediatR;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Source.Features.Users.Models;
using Source.Features.Workspaces.Commands;
using Source.Infrastructure.AuthorizationModels;

namespace Source.Infrastructure.Bootstrap;

/// <summary>
/// One-shot startup backfill that brings users created before P1.2 (the
/// <c>RegisterUserHandler</c> auto-workspace change) up to par with new signups.
///
/// <para><b>What it does.</b> Scans every non-deleted <see cref="User"/> row and:
///   <list type="number">
///     <item>If the user has no <see cref="Source.Features.Workspaces.Models.WorkspaceMembership"/> rows,
///       sends a <see cref="CreateWorkspaceCommand"/> via MediatR — the same code path
///       <c>RegisterUserHandler</c> uses, so we get a collision-safe slug, the Owner-role
///       membership, and the <c>WorkspaceCreated</c> domain event for free.</item>
///     <item>Ensures the user has the <see cref="RoleConstants.WorkspaceUser"/> Identity role.
///       Without this role the frontend's role-gated workspace app is invisible.</item>
///   </list>
/// </para>
///
/// <para><b>Idempotent.</b> Each criterion is checked individually; running the worker
/// twice in a row yields zero changes on the second pass.</para>
///
/// <para><b>Safety.</b> Each user is processed in its own try/catch so a single bad row
/// (e.g. a slug-collision race, a malformed email) cannot prevent the rest of the backfill
/// from completing or block API startup. The whole worker is also wrapped in an outer
/// catch — startup failures are logged loudly but never bubble.</para>
///
/// <para><b>Ordering.</b> Implemented as a <see cref="BackgroundService"/> so it runs
/// AFTER the host has finished starting (and therefore after migrations + role seeding
/// in <c>Program.cs</c>). We open a fresh DI scope because <see cref="UserManager{TUser}"/>,
/// <see cref="IMediator"/>, and <see cref="ApplicationDbContext"/> are all scoped while
/// hosted services are singletons.</para>
/// </summary>
public class ExistingUserWorkspaceBackfill : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ExistingUserWorkspaceBackfill> _logger;

    public ExistingUserWorkspaceBackfill(
        IServiceScopeFactory scopeFactory,
        ILogger<ExistingUserWorkspaceBackfill> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // BackgroundService.ExecuteAsync runs after the host has started, which means
        // migrations + role seeding in Program.cs have already completed by the time we
        // get here. The Yield is belt-and-braces to make sure we do not block startup.
        await Task.Yield();

        try
        {
            await RunAsync(stoppingToken);
        }
        catch (Exception ex)
        {
            // The whole backfill failing must NEVER prevent the API from running.
            _logger.LogError(ex,
                "ExistingUserWorkspaceBackfill failed; existing users may be missing workspaces. " +
                "Operators can re-run by restarting the API once the underlying issue is resolved.");
        }
    }

    /// <summary>
    /// The actual backfill loop. Public so it can be exercised directly from tests
    /// without spinning up a host. Each user is processed in isolation; one user's
    /// failure does not abort the rest of the run.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        _logger.LogInformation("ExistingUserWorkspaceBackfill: starting scan");

        // Pull every non-soft-deleted user. The query filter on User already excludes
        // IsDeleted=true rows, so AsNoTracking + ToList is safe and gives us a stable
        // snapshot to iterate without keeping a long-running DbContext attached to entities.
        var users = await db.Users
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        var backfilled = 0;
        var workspacesCreated = 0;
        var rolesGranted = 0;
        var skipped = 0;

        foreach (var snapshot in users)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                _logger.LogInformation("ExistingUserWorkspaceBackfill: cancellation requested, stopping early");
                break;
            }

            try
            {
                // Re-load the user via UserManager so the role APIs work against a tracked
                // entity attached to the same scope. (UserManager has its own DbContext.)
                var user = await userManager.FindByIdAsync(snapshot.Id);
                if (user is null)
                {
                    skipped++;
                    continue;
                }

                var hasMembership = await db.WorkspaceMemberships
                    .AsNoTracking()
                    .AnyAsync(m => m.UserId == user.Id, cancellationToken);

                var hasRole = await userManager.IsInRoleAsync(user, RoleConstants.WorkspaceUser);

                // Both conditions met — already bootstrapped, nothing to do.
                if (hasMembership && hasRole)
                {
                    skipped++;
                    continue;
                }

                var didSomething = false;

                if (!hasRole)
                {
                    var roleResult = await userManager.AddToRoleAsync(user, RoleConstants.WorkspaceUser);
                    if (roleResult.Succeeded)
                    {
                        rolesGranted++;
                        didSomething = true;
                    }
                    else
                    {
                        var errors = string.Join(", ", roleResult.Errors.Select(e => e.Description));
                        _logger.LogWarning(
                            "ExistingUserWorkspaceBackfill: failed to grant {Role} to user {UserId} ({Email}): {Errors}",
                            RoleConstants.WorkspaceUser, user.Id, user.Email, errors);
                    }
                }

                if (!hasMembership)
                {
                    var nameSeed = ExtractDisplayName(user.Email);
                    var createResult = await mediator.Send(
                        new CreateWorkspaceCommand(
                            OwnerUserId: user.Id,
                            Name: nameSeed,
                            Slug: null,
                            SlugSeed: user.Email),
                        cancellationToken);

                    if (createResult.IsSuccess)
                    {
                        workspacesCreated++;
                        didSomething = true;
                        _logger.LogInformation(
                            "ExistingUserWorkspaceBackfill: created primary workspace {Slug} for user {UserId} ({Email})",
                            createResult.Value.Slug, user.Id, user.Email);
                    }
                    else
                    {
                        _logger.LogWarning(
                            "ExistingUserWorkspaceBackfill: failed to auto-create workspace for user {UserId} ({Email}): {Error}",
                            user.Id, user.Email, createResult.Error);
                    }
                }

                if (didSomething)
                {
                    backfilled++;
                    _logger.LogInformation(
                        "ExistingUserWorkspaceBackfill: backfilled user {UserId} ({Email})",
                        user.Id, user.Email);
                }
                else
                {
                    skipped++;
                }
            }
            catch (Exception ex)
            {
                // A single user's failure must not abort the loop — log and continue.
                _logger.LogError(ex,
                    "ExistingUserWorkspaceBackfill: unexpected error backfilling user {UserId} ({Email}); skipping",
                    snapshot.Id, snapshot.Email);
                skipped++;
            }
        }

        _logger.LogInformation(
            "ExistingUserWorkspaceBackfill: complete — {Backfilled} users backfilled, " +
            "{WorkspacesCreated} workspaces created, {RolesGranted} roles granted, {Skipped} skipped",
            backfilled, workspacesCreated, rolesGranted, skipped);
    }

    /// <summary>
    /// Same algorithm as <c>RegisterUserHandler.ExtractDisplayName</c> — derive the
    /// initial workspace name from the email's local part and fall back to a generic
    /// label for malformed/empty addresses. Kept local rather than shared so the two
    /// code paths can diverge later without coupling.
    /// </summary>
    private static string ExtractDisplayName(string? email)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return "My workspace";
        }

        var at = email.IndexOf('@');
        var local = at > 0 ? email[..at] : email;
        return string.IsNullOrWhiteSpace(local) ? "My workspace" : local;
    }
}

using MediatR;
using Microsoft.AspNetCore.Identity;
using Source.Features.Users.Events;
using Source.Features.Users.Models;
using Source.Features.Workspaces.Commands;
using Source.Infrastructure.AuthorizationModels;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.Authentication.Commands;

public record RegisterUserCommand(string Email, string Password) : ICommand<Result<RegisterUserResponse>>;

public record RegisterUserResponse
{
    public required string UserId { get; init; }
    public required string Email { get; init; }
    public required string PrimaryWorkspaceSlug { get; init; }
}

public class RegisterUserHandler : ICommandHandler<RegisterUserCommand, Result<RegisterUserResponse>>
{
    private readonly UserManager<User> _userManager;
    private readonly IMediator _mediator;
    private readonly ILogger<RegisterUserHandler> _logger;

    public RegisterUserHandler(UserManager<User> userManager, IMediator mediator, ILogger<RegisterUserHandler> logger)
    {
        _userManager = userManager;
        _mediator = mediator;
        _logger = logger;
    }

    public async Task<Result<RegisterUserResponse>> Handle(RegisterUserCommand request, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Attempting to register user with email: {Email}", request.Email);

        var existingUser = await _userManager.FindByEmailAsync(request.Email);
        if (existingUser != null)
        {
            return Result.Failure<RegisterUserResponse>("User with this email already exists");
        }

        var user = new User
        {
            UserName = request.Email,
            Email = request.Email,
            EmailConfirmed = true
        };

        var result = await _userManager.CreateAsync(user, request.Password);
        if (!result.Succeeded)
        {
            var errors = string.Join(", ", result.Errors.Select(e => e.Description));
            _logger.LogWarning("Failed to register user {Email}: {Errors}", request.Email, errors);
            return Result.Failure<RegisterUserResponse>($"Registration failed: {errors}");
        }

        // Every signup gets the WorkspaceUser app role. Admin roles (SuperAdmin/TenantAdmin)
        // remain seeded-only; this role is what gates access to the /w/:slug app on the frontend.
        var roleResult = await _userManager.AddToRoleAsync(user, RoleConstants.WorkspaceUser);
        if (!roleResult.Succeeded)
        {
            _logger.LogWarning(
                "Could not assign {Role} to {Email}: {Errors}",
                RoleConstants.WorkspaceUser, request.Email,
                string.Join(", ", roleResult.Errors.Select(e => e.Description)));
            // Non-fatal — we keep the user, but log loudly. The role can be reconciled later.
        }

        // Auto-create their first workspace. Slug is derived from the email local-part by the
        // generator (with collision suffix); name defaults to the email local-part as well.
        var nameSeed = ExtractDisplayName(request.Email);
        var createWorkspace = await _mediator.Send(
            new CreateWorkspaceCommand(
                OwnerUserId: user.Id,
                Name: nameSeed,
                Slug: null,
                SlugSeed: request.Email),
            cancellationToken);

        if (!createWorkspace.IsSuccess)
        {
            // Roll forward — the user exists. Surface the underlying error so the caller can retry workspace.
            _logger.LogError(
                "User {UserId} created but workspace bootstrap failed: {Error}",
                user.Id, createWorkspace.Error);
            return Result.Failure<RegisterUserResponse>($"Account created but workspace setup failed: {createWorkspace.Error}");
        }

        _logger.LogInformation(
            "Successfully registered user {Email} with workspace {Slug}",
            request.Email, createWorkspace.Value.Slug);

        var userCreatedEvent = new UserCreated(user.Id, user.Email!, DateTime.UtcNow);
        await _mediator.Publish(userCreatedEvent, cancellationToken);

        return Result.Success(new RegisterUserResponse
        {
            UserId = user.Id,
            Email = user.Email!,
            PrimaryWorkspaceSlug = createWorkspace.Value.Slug,
        });
    }

    private static string ExtractDisplayName(string email)
    {
        var at = email.IndexOf('@');
        var local = at > 0 ? email[..at] : email;
        return string.IsNullOrWhiteSpace(local) ? "My workspace" : local;
    }
}

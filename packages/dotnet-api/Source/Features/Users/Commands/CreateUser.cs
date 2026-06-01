using Microsoft.AspNetCore.Identity;
using Source.Features.Users.Models;
using Source.Infrastructure.AuthorizationModels;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.Users.Commands;

public record CreateUserCommand : ICommand<Result<CreateUserResponse>>
{
    public string Email { get; init; } = string.Empty;
    public string? FirstName { get; init; }
    public string? LastName { get; init; }
}

public record CreateUserResponse
{
    public required string UserId { get; init; }
    public required string Email { get; init; }
    public required string FullName { get; init; }
}

public class CreateUserCommandHandler : ICommandHandler<CreateUserCommand, Result<CreateUserResponse>>
{
    private readonly UserManager<User> _userManager;
    private readonly ILogger<CreateUserCommandHandler> _logger;

    public CreateUserCommandHandler(UserManager<User> userManager, ILogger<CreateUserCommandHandler> logger)
    {
        _userManager = userManager;
        _logger = logger;
    }

    public async Task<Result<CreateUserResponse>> Handle(CreateUserCommand request, CancellationToken cancellationToken)
    {
        var existingUser = await _userManager.FindByEmailAsync(request.Email);
        if (existingUser != null)
            return Result.Failure<CreateUserResponse>("User with this email already exists");

        var user = new User
        {
            UserName = request.Email,
            Email = request.Email,
            EmailConfirmed = true,
            FirstName = request.FirstName,
            LastName = request.LastName
        };

        // Raise event before CreateAsync — interceptor persists + dispatches during SaveChanges
        user.OnCreated();

        var result = await _userManager.CreateAsync(user);
        if (!result.Succeeded)
        {
            var errors = string.Join(", ", result.Errors.Select(e => e.Description));
            return Result.Failure<CreateUserResponse>($"User creation failed: {errors}");
        }

        var roleResult = await _userManager.AddToRoleAsync(user, RoleConstants.SuperAdmin);
        if (!roleResult.Succeeded)
        {
            var errors = string.Join(", ", roleResult.Errors.Select(e => e.Description));
            _logger.LogError("Failed to assign SuperAdmin role to user {Email}: {Errors}", request.Email, errors);
        }
        else
        {
            _logger.LogInformation("Assigned SuperAdmin role to user {Email}", request.Email);
        }

        _logger.LogInformation("Successfully created user {Email} with ID {UserId}", request.Email, user.Id);

        var response = new CreateUserResponse { UserId = user.Id, Email = user.Email!, FullName = user.FullName };
        return Result.Success(response);
    }
}

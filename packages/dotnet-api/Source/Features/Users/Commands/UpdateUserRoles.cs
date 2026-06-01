using Microsoft.AspNetCore.Identity;
using Source.Features.Users.Models;
using Source.Infrastructure.AuthorizationModels;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.Users.Commands;

public record UpdateUserRolesCommand : ICommand<Result<UpdateUserRolesResponse>>
{
    public string UserId { get; init; } = string.Empty;
    public List<string> Roles { get; init; } = new();
}

public record UpdateUserRolesResponse
{
    public required string UserId { get; init; }
    public required string Email { get; init; }
    public required List<string> Roles { get; init; }
}

public class UpdateUserRolesHandler : ICommandHandler<UpdateUserRolesCommand, Result<UpdateUserRolesResponse>>
{
    private readonly UserManager<User> _userManager;
    private readonly ILogger<UpdateUserRolesHandler> _logger;

    public UpdateUserRolesHandler(UserManager<User> userManager, ILogger<UpdateUserRolesHandler> logger)
    {
        _userManager = userManager;
        _logger = logger;
    }

    public async Task<Result<UpdateUserRolesResponse>> Handle(UpdateUserRolesCommand request, CancellationToken cancellationToken)
    {
        var user = await _userManager.FindByIdAsync(request.UserId);
        if (user == null)
        {
            return Result.Failure<UpdateUserRolesResponse>("User not found");
        }

        if (user.IsDeleted)
        {
            return Result.Failure<UpdateUserRolesResponse>("Cannot update roles for deleted user");
        }

        var currentRoles = await _userManager.GetRolesAsync(user);
        var currentRolesList = currentRoles.ToList();
        var newRolesList = request.Roles.Distinct().ToList();

        var rolesToRemove = currentRolesList.Except(newRolesList).ToList();
        var rolesToAdd = newRolesList.Except(currentRolesList).ToList();

        foreach (var role in rolesToRemove)
        {
            var result = await _userManager.RemoveFromRoleAsync(user, role);
            if (!result.Succeeded)
            {
                var errors = string.Join(", ", result.Errors.Select(e => e.Description));
                _logger.LogWarning("Failed to remove role {Role} from user {UserId}: {Errors}", role, user.Id, errors);
            }
            else
            {
                _logger.LogInformation("Removed role {Role} from user {UserId}", role, user.Id);
            }
        }

        foreach (var role in rolesToAdd)
        {
            var result = await _userManager.AddToRoleAsync(user, role);
            if (!result.Succeeded)
            {
                var errors = string.Join(", ", result.Errors.Select(e => e.Description));
                _logger.LogWarning("Failed to add role {Role} to user {UserId}: {Errors}", role, user.Id, errors);
            }
            else
            {
                _logger.LogInformation("Added role {Role} to user {UserId}", role, user.Id);
            }
        }

        var updatedRoles = await _userManager.GetRolesAsync(user);
        _logger.LogInformation("Successfully updated roles for user {UserId}. Current roles: {Roles}", user.Id, string.Join(", ", updatedRoles));

        var response = new UpdateUserRolesResponse
        {
            UserId = user.Id,
            Email = user.Email!,
            Roles = updatedRoles.ToList()
        };
        return Result.Success(response);
    }
}


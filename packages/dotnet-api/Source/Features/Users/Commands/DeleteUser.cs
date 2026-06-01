using Microsoft.AspNetCore.Identity;
using Source.Features.Users.Models;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.Users.Commands;

public record DeleteUserCommand(string UserId) : ICommand<Result<DeleteUserResponse>>;

public record DeleteUserResponse
{
    public required string UserId { get; init; }
    public required DateTime DeletedAt { get; init; }
}

public class DeleteUserHandler : ICommandHandler<DeleteUserCommand, Result<DeleteUserResponse>>
{
    private readonly UserManager<User> _userManager;
    private readonly ILogger<DeleteUserHandler> _logger;

    public DeleteUserHandler(UserManager<User> userManager, ILogger<DeleteUserHandler> logger)
    {
        _userManager = userManager;
        _logger = logger;
    }

    public async Task<Result<DeleteUserResponse>> Handle(DeleteUserCommand request, CancellationToken cancellationToken)
    {
        var user = await _userManager.FindByIdAsync(request.UserId);
        if (user == null)
        {
            return Result.Failure<DeleteUserResponse>("User not found");
        }

        if (user.IsDeleted)
        {
            return Result.Failure<DeleteUserResponse>("User already deleted");
        }

        // Soft delete — DeletedAt, DeletedBy, UpdatedAt set automatically by DbContext
        user.IsDeleted = true;

        var result = await _userManager.UpdateAsync(user);
        if (!result.Succeeded)
        {
            var errors = string.Join(", ", result.Errors.Select(e => e.Description));
            return Result.Failure<DeleteUserResponse>($"Delete failed: {errors}");
        }

        _logger.LogInformation("Successfully soft deleted user {UserId}", user.Id);

        var response = new DeleteUserResponse { UserId = user.Id, DeletedAt = user.DeletedAt ?? DateTime.UtcNow };
        return Result.Success(response);
    }
} 
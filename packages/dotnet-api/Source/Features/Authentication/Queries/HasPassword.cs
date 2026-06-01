using Microsoft.AspNetCore.Identity;
using Source.Features.Users.Models;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.Authentication.Queries;

/// <summary>
/// Query to check if a user has a password set
/// </summary>
public record HasPasswordQuery(string UserId) : IQuery<Result<HasPasswordResponse>>;

/// <summary>
/// Response indicating whether the user has a password
/// </summary>
public record HasPasswordResponse
{
    public required bool HasPassword { get; init; }
}

/// <summary>
/// Handler for checking if user has a password
/// </summary>
public class HasPasswordHandler : IQueryHandler<HasPasswordQuery, Result<HasPasswordResponse>>
{
    private readonly UserManager<User> _userManager;
    private readonly ILogger<HasPasswordHandler> _logger;

    public HasPasswordHandler(UserManager<User> userManager, ILogger<HasPasswordHandler> logger)
    {
        _userManager = userManager;
        _logger = logger;
    }

    public async Task<Result<HasPasswordResponse>> Handle(HasPasswordQuery request, CancellationToken cancellationToken)
    {
        var user = await _userManager.FindByIdAsync(request.UserId);
        if (user == null)
        {
            _logger.LogWarning("Has password check for non-existent user: {UserId}", request.UserId);
            return Result.Failure<HasPasswordResponse>("User not found");
        }

        var hasPassword = await _userManager.HasPasswordAsync(user);

        var response = new HasPasswordResponse { HasPassword = hasPassword };
        return Result.Success(response);
    }
}

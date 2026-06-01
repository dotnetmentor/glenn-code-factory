using Microsoft.AspNetCore.Identity;
using Source.Features.Users.Models;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.Users.Queries;

public record GetUserQuery(string UserId) : IQuery<Result<UserResponse>>;

public record UserResponse
{
    public required string Id { get; init; }
    public required string Email { get; init; }
    public required string FirstName { get; init; }
    public required string LastName { get; init; }
    public required string FullName { get; init; }
    public required DateTime CreatedAt { get; init; }
    public required DateTime UpdatedAt { get; init; }
}

public class GetUserQueryHandler : IQueryHandler<GetUserQuery, Result<UserResponse>>
{
    private readonly UserManager<User> _userManager;
    private readonly ILogger<GetUserQueryHandler> _logger;

    public GetUserQueryHandler(UserManager<User> userManager, ILogger<GetUserQueryHandler> logger)
    {
        _userManager = userManager;
        _logger = logger;
    }

    public async Task<Result<UserResponse>> Handle(GetUserQuery request, CancellationToken cancellationToken)
    {
        var user = await _userManager.FindByIdAsync(request.UserId);
        if (user == null || user.IsDeleted)
        {
            return Result.Failure<UserResponse>("User not found");
        }

        var response = new UserResponse
        {
            Id = user.Id,
            Email = user.Email!,
            FirstName = user.FirstName ?? "",
            LastName = user.LastName ?? "",
            FullName = user.FullName,
            CreatedAt = user.CreatedAt,
            UpdatedAt = user.UpdatedAt
        };

        return Result.Success(response);
    }
} 
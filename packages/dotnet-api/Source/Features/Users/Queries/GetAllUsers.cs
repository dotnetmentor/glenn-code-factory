using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Source.Features.Users.Models;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.Users.Queries;

public record GetAllUsersQuery : IQuery<Result<GetAllUsersResponse>>
{
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 10;
    public string? Search { get; init; }
    public bool FetchAll { get; init; } = false;
}

public record GetAllUsersResponse
{
    public required List<UserListItem> Users { get; init; }
    public required int TotalCount { get; init; }
    public required int Page { get; init; }
    public required int PageSize { get; init; }
    public required int TotalPages { get; init; }
}

public record UserListItem
{
    public required string Id { get; init; }
    public required string Email { get; init; }
    public string? PhoneNumber { get; init; }
    public required string FullName { get; init; }
    public required DateTime CreatedAt { get; init; }
    public required List<string> Roles { get; init; }
}

public class GetAllUsersHandler : IQueryHandler<GetAllUsersQuery, Result<GetAllUsersResponse>>
{
    private readonly UserManager<User> _userManager;
    private readonly ILogger<GetAllUsersHandler> _logger;

    public GetAllUsersHandler(UserManager<User> userManager, ILogger<GetAllUsersHandler> logger)
    {
        _userManager = userManager;
        _logger = logger;
    }

    public async Task<Result<GetAllUsersResponse>> Handle(GetAllUsersQuery request, CancellationToken cancellationToken)
    {
        var query = _userManager.Users.AsQueryable();

        // Apply search filter
        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var searchTerm = request.Search.ToLower();
            query = query.Where(u => 
                u.Email!.ToLower().Contains(searchTerm) ||
                (u.FirstName != null && u.FirstName.ToLower().Contains(searchTerm)) ||
                (u.LastName != null && u.LastName.ToLower().Contains(searchTerm)) ||
                (u.PhoneNumber != null && u.PhoneNumber.Contains(searchTerm))
            );
        }

        var totalCount = await query.CountAsync(cancellationToken);
        
        // Apply pagination only if not fetching all
        var orderedQuery = query.OrderBy(u => u.CreatedAt);
        
        List<User> usersData;
        int totalPages;
        
        if (request.FetchAll)
        {
            // Fetch all with a safety limit of 10,000 users
            const int maxFetchLimit = 10000;
            if (totalCount > maxFetchLimit)
            {
                return Result.Failure<GetAllUsersResponse>($"Dataset too large to fetch all at once. Total count: {totalCount}, Max limit: {maxFetchLimit}. Please use filters to reduce the dataset.");
            }
            
            usersData = await orderedQuery.ToListAsync(cancellationToken);
            totalPages = 1;
        }
        else
        {
            totalPages = (int)Math.Ceiling((double)totalCount / request.PageSize);
            usersData = await orderedQuery
                .Skip((request.Page - 1) * request.PageSize)
                .Take(request.PageSize)
                .ToListAsync(cancellationToken);
        }

        // Fetch roles for each user
        var users = new List<UserListItem>();
        foreach (var user in usersData)
        {
            var roles = await _userManager.GetRolesAsync(user);
            users.Add(new UserListItem
            {
                Id = user.Id,
                Email = user.Email!,
                PhoneNumber = user.PhoneNumber,
                FullName = user.FullName,
                CreatedAt = user.CreatedAt,
                Roles = roles.ToList()
            });
        }

        var response = new GetAllUsersResponse
        {
            Users = users,
            TotalCount = totalCount,
            Page = request.Page,
            PageSize = request.PageSize,
            TotalPages = totalPages
        };

        return Result.Success(response);
    }
} 
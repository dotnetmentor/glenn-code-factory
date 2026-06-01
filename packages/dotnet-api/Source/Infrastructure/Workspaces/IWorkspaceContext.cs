using Source.Features.Workspaces.Models;

namespace Source.Infrastructure.Workspaces;

/// <summary>
/// Per-request workspace context populated by <see cref="RequireWorkspaceRoleAttribute"/>.
/// Inject this into command/query handlers that need to scope work to a specific workspace —
/// the filter has already validated the calling user can access this workspace at the requested
/// minimum role, so handlers don't need to redo any of those checks.
///
/// Outside of a workspace-scoped request the resolved properties will throw; check
/// <see cref="IsResolved"/> first if your handler can run in either context.
/// </summary>
public interface IWorkspaceContext
{
    bool IsResolved { get; }

    Guid Id { get; }
    string Slug { get; }
    WorkspaceRole Role { get; }
    bool IsSuperAdmin { get; }
    string UserId { get; }
}

internal sealed class WorkspaceContext : IWorkspaceContext
{
    public bool IsResolved { get; private set; }

    private Guid _id;
    private string _slug = string.Empty;
    private WorkspaceRole _role;
    private bool _isSuperAdmin;
    private string _userId = string.Empty;

    public Guid Id => Resolved(_id);
    public string Slug => Resolved(_slug);
    public WorkspaceRole Role => Resolved(_role);
    public bool IsSuperAdmin => Resolved(_isSuperAdmin);
    public string UserId => Resolved(_userId);

    public void Set(Guid id, string slug, WorkspaceRole role, bool isSuperAdmin, string userId)
    {
        _id = id;
        _slug = slug;
        _role = role;
        _isSuperAdmin = isSuperAdmin;
        _userId = userId;
        IsResolved = true;
    }

    private T Resolved<T>(T value)
    {
        if (!IsResolved)
        {
            throw new InvalidOperationException(
                "IWorkspaceContext is not resolved. This handler ran outside a route protected by " +
                "[RequireWorkspaceRole]. Either decorate the endpoint or guard with IsResolved.");
        }
        return value;
    }
}

using Source.Features.Workspaces.Models;

namespace Api.Tests.Features.Workspaces;

/// <summary>
/// Pinned semantics for the workspace RBAC privilege ordering. Lower numeric value =
/// higher privilege (Owner=0 ⊃ Admin=1 ⊃ Member=2). The whole authorization filter
/// stack relies on <see cref="WorkspaceRoleExtensions.IsAtLeast"/> doing the right
/// thing here, so any change should break these tests first.
/// </summary>
public class WorkspaceRoleTests
{
    [Theory]
    [InlineData(WorkspaceRole.Owner, WorkspaceRole.Owner, true)]
    [InlineData(WorkspaceRole.Owner, WorkspaceRole.Admin, true)]
    [InlineData(WorkspaceRole.Owner, WorkspaceRole.Member, true)]
    [InlineData(WorkspaceRole.Admin, WorkspaceRole.Admin, true)]
    [InlineData(WorkspaceRole.Admin, WorkspaceRole.Member, true)]
    [InlineData(WorkspaceRole.Admin, WorkspaceRole.Owner, false)]
    [InlineData(WorkspaceRole.Member, WorkspaceRole.Member, true)]
    [InlineData(WorkspaceRole.Member, WorkspaceRole.Admin, false)]
    [InlineData(WorkspaceRole.Member, WorkspaceRole.Owner, false)]
    public void IsAtLeast_returns_expected_for_role_pair(WorkspaceRole actual, WorkspaceRole required, bool expected)
    {
        actual.IsAtLeast(required).Should().Be(expected);
    }
}

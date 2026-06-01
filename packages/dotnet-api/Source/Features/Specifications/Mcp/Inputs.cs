namespace Source.Features.Specifications.Mcp;

/// <summary>
/// Wire-level input records for the specifications MCP. Thin DTOs the daemon
/// posts as the body of each MCP method call. They deliberately do <b>not</b>
/// carry <c>ProjectId</c> / <c>RuntimeId</c> / <c>TenantId</c> — those are
/// server-derived from the runtime token claim. If a malicious or buggy client
/// adds one, <see cref="Source.Features.Mcp.Framework.McpControllerBase"/>'s
/// forbidden-field strip zeroes it before the handler runs (with a structured
/// warning).
///
/// <para><b>Why mutable records.</b> The framework uses reflection to clear
/// forbidden fields on the input. Init-only properties would prevent the strip.
/// We accept the mild mutability tradeoff because the input lifetime is one
/// request and the records are not shared across handlers. Mirrors the
/// <see cref="Source.Features.ProjectKanban.Mcp"/> precedent.</para>
/// </summary>
public record SaveSpecificationInput
{
    public string Slug { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
}

public record ReadSpecificationInput
{
    public string Slug { get; set; } = string.Empty;
}

public record DeleteSpecificationInput
{
    public string Slug { get; set; } = string.Empty;
}

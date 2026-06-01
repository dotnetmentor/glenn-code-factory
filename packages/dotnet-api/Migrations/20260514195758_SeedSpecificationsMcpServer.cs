using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace api.Migrations
{
    /// <inheritdoc />
    public partial class SeedSpecificationsMcpServer : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Seed the specifications MCP catalog row so it's emitted by
            // GetBootstrapQuery.LoadMcpServersAsync (which filters on
            // DefaultEnabled = true). Mirrors the kanban precedent in
            // 20260509022338_AddMcpAndProjectKanban. Fixed Guid so seed is
            // deterministic across deployments. CreatedAt / UpdatedAt set to a
            // fixed UTC timestamp so the migration is reproducible (the
            // SaveChanges-based audit interceptor doesn't run for raw seeds).
            //
            // Name + version mirror the SpecificationsMcpController's
            // [McpServer(name: "specifications", version: "v1")] attribute and
            // the Route("api/mcp/specifications/v1"). The daemon's bootstrap stage
            // composes the URL as {publicApi}/api/mcp/{name}/{version} from those
            // two fields, so any change here MUST track the controller. The
            // /api/ prefix is mandatory — the production Cloudflare tunnel
            // forwards only /api/* to the backend.
            migrationBuilder.InsertData(
                table: "McpServers",
                columns: new[] { "Id", "Name", "Version", "DefaultEnabled", "CreatedAt", "UpdatedAt", "IsDeleted", "DeletedAt", "DeletedBy" },
                values: new object[]
                {
                    Guid.Parse("8c2e1f4d-3b6a-4e9c-9f78-2d0a5b1c8e3f"),
                    "specifications",
                    "v1",
                    true,
                    new DateTime(2026, 5, 14, 0, 0, 0, DateTimeKind.Utc),
                    new DateTime(2026, 5, 14, 0, 0, 0, DateTimeKind.Utc),
                    false,
                    null,
                    null,
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "McpServers",
                keyColumn: "Id",
                keyValue: Guid.Parse("8c2e1f4d-3b6a-4e9c-9f78-2d0a5b1c8e3f"));
        }
    }
}

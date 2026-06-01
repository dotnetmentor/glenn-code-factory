using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace api.Migrations
{
    /// <inheritdoc />
    public partial class AddRuntimeDiskPressureEvent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RuntimeDiskPressureEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RuntimeId = table.Column<Guid>(type: "uuid", nullable: false),
                    Level = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    UsedBytes = table.Column<long>(type: "bigint", nullable: false),
                    TotalBytes = table.Column<long>(type: "bigint", nullable: false),
                    UsedPct = table.Column<double>(type: "double precision", nullable: false),
                    SampledAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ReportedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RuntimeDiskPressureEvents", x => x.Id);
                });

            // "Disk-pressure timeline for this runtime" — CreatedAt sorted
            // DESC so range scans pick up newest rows first. EF Core 9 doesn't
            // expose per-column sort direction on relational indexes, so we
            // emit the index via raw SQL (Postgres syntax). Same idiom as the
            // RuntimeErrorReport / BootstrapRun / RuntimeStateEvent indexes.
            migrationBuilder.Sql(
                "CREATE INDEX \"IX_RuntimeDiskPressureEvents_RuntimeId_CreatedAt\" " +
                "ON \"RuntimeDiskPressureEvents\" (\"RuntimeId\", \"CreatedAt\" DESC);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "DROP INDEX IF EXISTS \"IX_RuntimeDiskPressureEvents_RuntimeId_CreatedAt\";");

            migrationBuilder.DropTable(
                name: "RuntimeDiskPressureEvents");
        }
    }
}

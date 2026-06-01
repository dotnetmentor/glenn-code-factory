using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace api.Migrations
{
    /// <summary>
    /// Appends a single row to the curated <c>OpencodeModels</c> catalog seeded
    /// by <c>ReplaceOpencodeModelsSeed</c>: <c>Gemini 3.5 Flash</c>
    /// (slug <c>gemini-3.5-flash</c>, Id <c>b0000000-...-000042</c> — the next
    /// sequential after <c>-000041</c> "Trinity Large Preview").
    ///
    /// <para><c>ReasoningOptions</c> is intentionally left null, matching the
    /// rest of the Gemini family — see <c>AddOpencodeModelReasoningOptions</c>
    /// for the rationale (the opencode catalog doesn't expose a per-model
    /// options override for Gemini's reasoning surface today).</para>
    /// </summary>
    /// <inheritdoc />
    public partial class AddGemini35FlashModel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // IAuditable interceptor does not run for InsertData, so timestamps
            // are set explicitly. Mirrors the columns array used by the original
            // seed migration; ReasoningOptions defaults to NULL (Gemini family).
            var ts = new DateTime(2026, 5, 19, 0, 0, 0, DateTimeKind.Utc);

            migrationBuilder.InsertData(
                table: "OpencodeModels",
                columns: new[] { "Id", "CreatedAt", "DeletedAt", "DeletedBy", "DisplayName", "IsActive", "IsDeleted", "Slug", "UpdatedAt" },
                values: new object[,]
                {
                    { new Guid("b0000000-0000-0000-0000-000000000042"), ts, null, null, "Gemini 3.5 Flash", true, false, "gemini-3.5-flash", ts }
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "OpencodeModels",
                keyColumn: "Id",
                keyValue: new Guid("b0000000-0000-0000-0000-000000000042"));
        }
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace api.Migrations
{
    /// <inheritdoc />
    public partial class ReplaceOpencodeModelsSeed : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Hard-delete the original 5 seeded rows plus any ad-hoc rows that landed
            // in the a0000000-... namespace via manual inserts (e.g. -000006 added out
            // of band before the curated catalog landed). FKs from Projects.OpencodeModelId
            // and AgentSessions.OpencodeModelId are configured ON DELETE SET NULL, so
            // any outstanding references nullify automatically. Wiping the slugs that
            // overlap with the new catalog also avoids a unique-index collision on
            // IX_OpencodeModels_Slug (partial index where IsDeleted = false).
            migrationBuilder.Sql(
                "DELETE FROM \"OpencodeModels\" WHERE \"Id\"::text LIKE 'a0000000-0000-0000-0000-%';");

            // Insert the curated catalog (41 rows). The IAuditable interceptor does
            // not run for InsertData so CreatedAt / UpdatedAt are set explicitly.
            var ts = new DateTime(2026, 5, 17, 0, 0, 0, DateTimeKind.Utc);

            migrationBuilder.InsertData(
                table: "OpencodeModels",
                columns: new[] { "Id", "CreatedAt", "DeletedAt", "DeletedBy", "DisplayName", "IsActive", "IsDeleted", "Slug", "UpdatedAt" },
                values: new object[,]
                {
                    { new Guid("b0000000-0000-0000-0000-000000000001"), ts, null, null, "Big Pickle", true, false, "big-pickle", ts },
                    { new Guid("b0000000-0000-0000-0000-000000000002"), ts, null, null, "MiniMax M2.5", true, false, "minimax-m2.5", ts },
                    { new Guid("b0000000-0000-0000-0000-000000000003"), ts, null, null, "MiniMax M2.5 Free", true, false, "minimax-m2.5-free", ts },
                    { new Guid("b0000000-0000-0000-0000-000000000004"), ts, null, null, "MiniMax M2.7", true, false, "minimax-m2.7", ts },
                    { new Guid("b0000000-0000-0000-0000-000000000005"), ts, null, null, "Claude Haiku 4.5", true, false, "claude-haiku-4.5", ts },
                    { new Guid("b0000000-0000-0000-0000-000000000006"), ts, null, null, "Claude Opus 4.1", true, false, "claude-opus-4.1", ts },
                    { new Guid("b0000000-0000-0000-0000-000000000007"), ts, null, null, "Claude Opus 4.5", true, false, "claude-opus-4.5", ts },
                    { new Guid("b0000000-0000-0000-0000-000000000008"), ts, null, null, "Claude Opus 4.6", true, false, "claude-opus-4.6", ts },
                    { new Guid("b0000000-0000-0000-0000-000000000009"), ts, null, null, "Claude Opus 4.7", true, false, "claude-opus-4.7", ts },
                    { new Guid("b0000000-0000-0000-0000-000000000010"), ts, null, null, "Claude Sonnet 4", true, false, "claude-sonnet-4", ts },
                    { new Guid("b0000000-0000-0000-0000-000000000011"), ts, null, null, "Claude Sonnet 4.5", true, false, "claude-sonnet-4-5", ts },
                    { new Guid("b0000000-0000-0000-0000-000000000012"), ts, null, null, "Claude Sonnet 4.6", true, false, "claude-sonnet-4.6", ts },
                    { new Guid("b0000000-0000-0000-0000-000000000013"), ts, null, null, "GPT 5", true, false, "gpt-5", ts },
                    { new Guid("b0000000-0000-0000-0000-000000000014"), ts, null, null, "GPT 5 Codex", true, false, "gpt-5-codex", ts },
                    { new Guid("b0000000-0000-0000-0000-000000000015"), ts, null, null, "GPT 5 Nano", true, false, "gpt-5-nano", ts },
                    { new Guid("b0000000-0000-0000-0000-000000000016"), ts, null, null, "GPT 5.1", true, false, "gpt-5.1", ts },
                    { new Guid("b0000000-0000-0000-0000-000000000017"), ts, null, null, "GPT 5.1 Codex", true, false, "gpt-5.1-codex", ts },
                    { new Guid("b0000000-0000-0000-0000-000000000018"), ts, null, null, "GPT 5.1 Codex Max", true, false, "gpt-5.1-codex-max", ts },
                    { new Guid("b0000000-0000-0000-0000-000000000019"), ts, null, null, "GPT 5.1 Codex Mini", true, false, "gpt-5.1-codex-mini", ts },
                    { new Guid("b0000000-0000-0000-0000-000000000020"), ts, null, null, "GPT 5.2", true, false, "gpt-5.2", ts },
                    { new Guid("b0000000-0000-0000-0000-000000000021"), ts, null, null, "GPT 5.2 Codex", true, false, "gpt-5.2-codex", ts },
                    { new Guid("b0000000-0000-0000-0000-000000000022"), ts, null, null, "GPT 5.3 Codex", true, false, "gpt-5.3-codex", ts },
                    { new Guid("b0000000-0000-0000-0000-000000000023"), ts, null, null, "GPT 5.3 Codex Spark", true, false, "gpt-5.3-codex-spark", ts },
                    { new Guid("b0000000-0000-0000-0000-000000000024"), ts, null, null, "GPT 5.4", true, false, "gpt-5.4", ts },
                    { new Guid("b0000000-0000-0000-0000-000000000025"), ts, null, null, "GPT 5.4 Mini", true, false, "gpt-5.4-mini", ts },
                    { new Guid("b0000000-0000-0000-0000-000000000026"), ts, null, null, "GPT 5.4 Nano", true, false, "gpt-5.4-nano", ts },
                    { new Guid("b0000000-0000-0000-0000-000000000027"), ts, null, null, "GPT 5.4 Pro", true, false, "gpt-5.4-pro", ts },
                    { new Guid("b0000000-0000-0000-0000-000000000028"), ts, null, null, "GPT 5.5", true, false, "gpt-5.5", ts },
                    { new Guid("b0000000-0000-0000-0000-000000000029"), ts, null, null, "GPT 5.5 Pro", true, false, "gpt-5.5-pro", ts },
                    { new Guid("b0000000-0000-0000-0000-000000000030"), ts, null, null, "Gemini 3 Flash", true, false, "gemini-3-flash", ts },
                    { new Guid("b0000000-0000-0000-0000-000000000031"), ts, null, null, "Gemini 3.1 Pro", true, false, "gemini-3.1-pro", ts },
                    { new Guid("b0000000-0000-0000-0000-000000000032"), ts, null, null, "DeepSeek V4 Flash Free", true, false, "deepseek-v4-flash-free", ts },
                    { new Guid("b0000000-0000-0000-0000-000000000033"), ts, null, null, "GLM 5", true, false, "glm-5", ts },
                    { new Guid("b0000000-0000-0000-0000-000000000034"), ts, null, null, "GLM 5.1", true, false, "glm-5.1", ts },
                    { new Guid("b0000000-0000-0000-0000-000000000035"), ts, null, null, "Kimi K2.5", true, false, "kimi-k2.5", ts },
                    { new Guid("b0000000-0000-0000-0000-000000000036"), ts, null, null, "Kimi K2.6", true, false, "kimi-k2.6", ts },
                    { new Guid("b0000000-0000-0000-0000-000000000037"), ts, null, null, "Nemotron 3 Super Free", true, false, "nemotron-3-super-free", ts },
                    { new Guid("b0000000-0000-0000-0000-000000000038"), ts, null, null, "Qwen3.5 Plus", true, false, "qwen3.5-plus", ts },
                    { new Guid("b0000000-0000-0000-0000-000000000039"), ts, null, null, "Qwen3.6 Plus", true, false, "qwen3.6-plus", ts },
                    { new Guid("b0000000-0000-0000-0000-000000000040"), ts, null, null, "Ring 2.6 1T Free", true, false, "ring-2.6-1t-free", ts },
                    { new Guid("b0000000-0000-0000-0000-000000000041"), ts, null, null, "Trinity Large Preview", true, false, "trinity-large-preview", ts }
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Remove the 41 curated rows.
            migrationBuilder.DeleteData(
                table: "OpencodeModels",
                keyColumn: "Id",
                keyValues: new object[]
                {
                    new Guid("b0000000-0000-0000-0000-000000000001"),
                    new Guid("b0000000-0000-0000-0000-000000000002"),
                    new Guid("b0000000-0000-0000-0000-000000000003"),
                    new Guid("b0000000-0000-0000-0000-000000000004"),
                    new Guid("b0000000-0000-0000-0000-000000000005"),
                    new Guid("b0000000-0000-0000-0000-000000000006"),
                    new Guid("b0000000-0000-0000-0000-000000000007"),
                    new Guid("b0000000-0000-0000-0000-000000000008"),
                    new Guid("b0000000-0000-0000-0000-000000000009"),
                    new Guid("b0000000-0000-0000-0000-000000000010"),
                    new Guid("b0000000-0000-0000-0000-000000000011"),
                    new Guid("b0000000-0000-0000-0000-000000000012"),
                    new Guid("b0000000-0000-0000-0000-000000000013"),
                    new Guid("b0000000-0000-0000-0000-000000000014"),
                    new Guid("b0000000-0000-0000-0000-000000000015"),
                    new Guid("b0000000-0000-0000-0000-000000000016"),
                    new Guid("b0000000-0000-0000-0000-000000000017"),
                    new Guid("b0000000-0000-0000-0000-000000000018"),
                    new Guid("b0000000-0000-0000-0000-000000000019"),
                    new Guid("b0000000-0000-0000-0000-000000000020"),
                    new Guid("b0000000-0000-0000-0000-000000000021"),
                    new Guid("b0000000-0000-0000-0000-000000000022"),
                    new Guid("b0000000-0000-0000-0000-000000000023"),
                    new Guid("b0000000-0000-0000-0000-000000000024"),
                    new Guid("b0000000-0000-0000-0000-000000000025"),
                    new Guid("b0000000-0000-0000-0000-000000000026"),
                    new Guid("b0000000-0000-0000-0000-000000000027"),
                    new Guid("b0000000-0000-0000-0000-000000000028"),
                    new Guid("b0000000-0000-0000-0000-000000000029"),
                    new Guid("b0000000-0000-0000-0000-000000000030"),
                    new Guid("b0000000-0000-0000-0000-000000000031"),
                    new Guid("b0000000-0000-0000-0000-000000000032"),
                    new Guid("b0000000-0000-0000-0000-000000000033"),
                    new Guid("b0000000-0000-0000-0000-000000000034"),
                    new Guid("b0000000-0000-0000-0000-000000000035"),
                    new Guid("b0000000-0000-0000-0000-000000000036"),
                    new Guid("b0000000-0000-0000-0000-000000000037"),
                    new Guid("b0000000-0000-0000-0000-000000000038"),
                    new Guid("b0000000-0000-0000-0000-000000000039"),
                    new Guid("b0000000-0000-0000-0000-000000000040"),
                    new Guid("b0000000-0000-0000-0000-000000000041")
                });

            // Restore the original 5 seeded rows with their original timestamps.
            var originalTs = new DateTime(2026, 5, 15, 0, 0, 0, 0, DateTimeKind.Utc);

            migrationBuilder.InsertData(
                table: "OpencodeModels",
                columns: new[] { "Id", "CreatedAt", "DeletedAt", "DeletedBy", "DisplayName", "IsActive", "IsDeleted", "Slug", "UpdatedAt" },
                values: new object[,]
                {
                    { new Guid("a0000000-0000-0000-0000-000000000001"), originalTs, null, null, "MiniMax M2.5 (Free)", true, false, "minimax-m2.5-free", originalTs },
                    { new Guid("a0000000-0000-0000-0000-000000000002"), originalTs, null, null, "Ring 2.6 1T (Free)", true, false, "ring-2.6-1t-free", originalTs },
                    { new Guid("a0000000-0000-0000-0000-000000000003"), originalTs, null, null, "MiniMax M2.7", true, false, "minimax-m2.7", originalTs },
                    { new Guid("a0000000-0000-0000-0000-000000000004"), originalTs, null, null, "GPT-5.4 Mini", true, false, "gpt-5.4-mini", originalTs },
                    { new Guid("a0000000-0000-0000-0000-000000000005"), originalTs, null, null, "Qwen 3.5 Plus", true, false, "qwen-3.5-plus", originalTs }
                });
        }
    }
}

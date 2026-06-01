using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace api.Migrations
{
    /// <inheritdoc />
    public partial class AddAttachments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Idempotent by design — the chat-file-attachments table may already
            // exist in some databases (it shipped earlier on a feature branch
            // that was partially applied before this consolidated recovery
            // migration). CREATE TABLE IF NOT EXISTS + guarded FK/index DDL let
            // this migration run cleanly on both a fresh DB and one where the
            // table is already present, without throwing "already exists".

            migrationBuilder.Sql(@"
                CREATE TABLE IF NOT EXISTS ""Attachments"" (
                    ""Id"" uuid NOT NULL,
                    ""ConversationId"" uuid NOT NULL,
                    ""FileName"" character varying(512) NOT NULL,
                    ""ContentType"" character varying(256) NULL,
                    ""SizeBytes"" bigint NOT NULL,
                    ""R2Key"" character varying(1024) NOT NULL,
                    ""UploadedAt"" timestamp with time zone NULL,
                    ""StagedAt"" timestamp with time zone NULL,
                    ""SessionId"" uuid NULL,
                    ""CreatedAt"" timestamp with time zone NOT NULL,
                    ""UpdatedAt"" timestamp with time zone NOT NULL,
                    ""IsDeleted"" boolean NOT NULL,
                    ""DeletedAt"" timestamp with time zone NULL,
                    ""DeletedBy"" text NULL,
                    CONSTRAINT ""PK_Attachments"" PRIMARY KEY (""Id"")
                );
            ");

            // FK → Conversations (Restrict). Guarded so a re-run / pre-existing
            // table doesn't blow up on a duplicate constraint.
            migrationBuilder.Sql(@"
                DO $$
                BEGIN
                    IF NOT EXISTS (
                        SELECT 1 FROM pg_constraint
                        WHERE conname = 'FK_Attachments_Conversations_ConversationId'
                    ) THEN
                        ALTER TABLE ""Attachments""
                            ADD CONSTRAINT ""FK_Attachments_Conversations_ConversationId""
                            FOREIGN KEY (""ConversationId"") REFERENCES ""Conversations"" (""Id"")
                            ON DELETE RESTRICT;
                    END IF;
                END $$;
            ");

            // FK → AgentSessions (SetNull).
            migrationBuilder.Sql(@"
                DO $$
                BEGIN
                    IF NOT EXISTS (
                        SELECT 1 FROM pg_constraint
                        WHERE conname = 'FK_Attachments_AgentSessions_SessionId'
                    ) THEN
                        ALTER TABLE ""Attachments""
                            ADD CONSTRAINT ""FK_Attachments_AgentSessions_SessionId""
                            FOREIGN KEY (""SessionId"") REFERENCES ""AgentSessions"" (""Id"")
                            ON DELETE SET NULL;
                    END IF;
                END $$;
            ");

            migrationBuilder.Sql(@"
                CREATE INDEX IF NOT EXISTS ""IX_Attachments_ConversationId""
                    ON ""Attachments"" (""ConversationId"");
            ");

            migrationBuilder.Sql(@"
                CREATE INDEX IF NOT EXISTS ""IX_Attachments_SessionId""
                    ON ""Attachments"" (""SessionId"")
                    WHERE ""SessionId"" IS NOT NULL;
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"DROP TABLE IF EXISTS ""Attachments"";");
        }
    }
}

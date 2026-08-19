using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PlexRequestsHosted.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class HardenDiscordBridgeOutbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "AcknowledgedAt",
                table: "BridgeOutbox",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DeduplicationKey",
                table: "BridgeOutbox",
                type: "TEXT",
                maxLength: 96,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ExternalId",
                table: "BridgeOutbox",
                type: "TEXT",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExternalSource",
                table: "BridgeOutbox",
                type: "TEXT",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MediaKind",
                table: "BridgeOutbox",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "RequestScopeKind",
                table: "BridgeOutbox",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            // Existing rows predate immutable event identities. Give every one a unique legacy key so the
            // unique index can be added without discarding history, then snapshot provider-neutral request
            // identity while the related request is still available.
            migrationBuilder.Sql("""
                UPDATE BridgeOutbox
                SET DeduplicationKey = 'legacy:' || Id,
                    RequestScopeKind = COALESCE(
                        (SELECT r.RequestScopeKind FROM MediaRequests r WHERE r.Id = BridgeOutbox.MediaRequestId),
                        CASE MediaType WHEN 1 THEN 1 WHEN 2 THEN 4 WHEN 3 THEN 1 ELSE 0 END),
                    ExternalSource = (SELECT r.ExternalSource FROM MediaRequests r WHERE r.Id = BridgeOutbox.MediaRequestId),
                    ExternalId = (SELECT r.ExternalId FROM MediaRequests r WHERE r.Id = BridgeOutbox.MediaRequestId);

                UPDATE BridgeOutbox
                SET MediaKind = CASE RequestScopeKind
                    WHEN 4 THEN 2
                    WHEN 5 THEN 3
                    WHEN 6 THEN 4
                    ELSE CASE MediaType WHEN 1 THEN 1 WHEN 2 THEN 2 WHEN 3 THEN 1 ELSE 0 END
                END;
                """);

            // Earlier code tried to enforce one Discord account in application code, but concurrent link
            // attempts could still leave duplicates. Normalize ids and deterministically retain the newest
            // profile before making that invariant a database constraint.
            migrationBuilder.Sql("""
                UPDATE UserProfiles
                SET DiscordUserId = NULL, DiscordUsername = NULL, DiscordDmOptIn = 0
                WHERE DiscordUserId IS NOT NULL AND TRIM(DiscordUserId) = '';

                UPDATE UserProfiles SET DiscordUserId = TRIM(DiscordUserId) WHERE DiscordUserId IS NOT NULL;

                UPDATE UserProfiles
                SET DiscordUserId = NULL, DiscordUsername = NULL, DiscordDmOptIn = 0
                WHERE DiscordUserId IS NOT NULL
                  AND Id NOT IN (
                      SELECT MAX(Id) FROM UserProfiles
                      WHERE DiscordUserId IS NOT NULL
                      GROUP BY DiscordUserId
                  );
                """);

            migrationBuilder.CreateIndex(
                name: "IX_UserProfiles_DiscordUserId",
                table: "UserProfiles",
                column: "DiscordUserId",
                unique: true,
                filter: "\"DiscordUserId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_BridgeOutbox_AcknowledgedAt_Id",
                table: "BridgeOutbox",
                columns: new[] { "AcknowledgedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_BridgeOutbox_DeduplicationKey",
                table: "BridgeOutbox",
                column: "DeduplicationKey",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_UserProfiles_DiscordUserId",
                table: "UserProfiles");

            migrationBuilder.DropIndex(
                name: "IX_BridgeOutbox_AcknowledgedAt_Id",
                table: "BridgeOutbox");

            migrationBuilder.DropIndex(
                name: "IX_BridgeOutbox_DeduplicationKey",
                table: "BridgeOutbox");

            migrationBuilder.DropColumn(
                name: "AcknowledgedAt",
                table: "BridgeOutbox");

            migrationBuilder.DropColumn(
                name: "DeduplicationKey",
                table: "BridgeOutbox");

            migrationBuilder.DropColumn(
                name: "ExternalId",
                table: "BridgeOutbox");

            migrationBuilder.DropColumn(
                name: "ExternalSource",
                table: "BridgeOutbox");

            migrationBuilder.DropColumn(
                name: "MediaKind",
                table: "BridgeOutbox");

            migrationBuilder.DropColumn(
                name: "RequestScopeKind",
                table: "BridgeOutbox");
        }
    }
}

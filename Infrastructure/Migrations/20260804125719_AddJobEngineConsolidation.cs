using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PlexRequestsHosted.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddJobEngineConsolidation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ConsecutiveFailures",
                table: "ScheduledJobs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "LastRunDurationMs",
                table: "ScheduledJobs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "RunningSince",
                table: "ScheduledJobs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TimeoutSeconds",
                table: "ScheduledJobs",
                type: "INTEGER",
                nullable: false,
                // Matches the entity default. EF would have used 0 here, which the scheduler clamps to its
                // 60s floor — every pre-existing schedule row would have been given a one-minute run cap.
                defaultValue: 1800);

            migrationBuilder.AddColumn<int>(
                name: "MissedScans",
                table: "PlexSeasonAvailability",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "MissedScans",
                table: "PlexMappings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "CutoffState",
                table: "MediaRequests",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_MediaRequests_Status_MediaType",
                table: "MediaRequests",
                columns: new[] { "Status", "MediaType" });

            // Carry the old boolean forward. Without this every existing request lands on Unknown (0) and the
            // upgrade scan — which now looks for Unmet — would stop seeing the real candidates. CutoffMet is
            // left in place as a mirror so a rollback still has its data.
            //   0 = Unknown, 1 = Met, 2 = Unmet  (Shared.Enums.CutoffState)
            migrationBuilder.Sql(
                "UPDATE MediaRequests SET CutoffState = CASE WHEN CutoffMet = 1 THEN 1 ELSE 2 END;");

            // Requests that never imported anything with a known resolution can't honestly be called Met or
            // Unmet — reset those to Unknown so they're re-derived rather than inheriting a guess.
            migrationBuilder.Sql(@"
                UPDATE MediaRequests SET CutoffState = 0, CutoffMet = 0
                WHERE NOT EXISTS (
                    SELECT 1 FROM ImportedFiles f
                    JOIN FulfillmentJobs j ON j.Id = f.FulfillmentJobId
                    WHERE j.MediaRequestId = MediaRequests.Id
                      AND f.FileType = 'video' AND f.ResolutionHeight > 0);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MediaRequests_Status_MediaType",
                table: "MediaRequests");

            migrationBuilder.DropColumn(
                name: "ConsecutiveFailures",
                table: "ScheduledJobs");

            migrationBuilder.DropColumn(
                name: "LastRunDurationMs",
                table: "ScheduledJobs");

            migrationBuilder.DropColumn(
                name: "RunningSince",
                table: "ScheduledJobs");

            migrationBuilder.DropColumn(
                name: "TimeoutSeconds",
                table: "ScheduledJobs");

            migrationBuilder.DropColumn(
                name: "MissedScans",
                table: "PlexSeasonAvailability");

            migrationBuilder.DropColumn(
                name: "MissedScans",
                table: "PlexMappings");

            migrationBuilder.DropColumn(
                name: "CutoffState",
                table: "MediaRequests");
        }
    }
}

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PlexRequestsHosted.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDurableMonitorChildren : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_FulfillmentJobs_MediaRequestId",
                table: "FulfillmentJobs");

            migrationBuilder.AddColumn<int>(
                name: "MonitoringAnchorId",
                table: "MediaRequests",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MonitoringSeasonNumber",
                table: "MediaRequests",
                type: "INTEGER",
                nullable: true);

            // CreateRequestCore wrote only the legacy Monitored bit after MonitorMode became authoritative.
            // Recover every affected TV request as AllEpisodes (1) and retain its original monitoring start.
            migrationBuilder.Sql("""
                UPDATE MediaRequests
                SET MonitorMode = 1,
                    MonitoredSince = COALESCE(MonitoredSince, RequestedAt)
                WHERE MediaType = 1 AND Monitored = 1 AND MonitorMode = 0;
                """);

            // Rows handed to a job used to lose their wake time forever, and hitting the attempt cap marked
            // them Skipped. Re-arm both legacy dead-end states; the monitor will verify live-job coverage.
            migrationBuilder.Sql("""
                UPDATE AirSchedule
                SET SearchState = 1,
                    NextSearchAt = CURRENT_TIMESTAMP,
                    UpdatedAt = CURRENT_TIMESTAMP
                WHERE Monitored = 1 AND HasFile = 0 AND (SearchState IN (2, 4) OR NextSearchAt IS NULL);
                """);

            // Remove the exact no-job duplicates produced by the old append-before-enqueue monitor loop.
            // They contain no fulfillment history and a live broader monitor is authoritative for the same
            // user/title, so retaining them only pollutes Requests and permanently blocks re-requests.
            migrationBuilder.Sql("""
                DELETE FROM MediaRequests AS child
                WHERE child.MediaType = 1
                  AND child.Status = 2
                  AND child.Monitored = 0
                  AND child.RequestAllSeasons = 0
                  AND child.RequestedEpisodesCsv IS NOT NULL
                  AND NOT EXISTS (SELECT 1 FROM FulfillmentJobs AS job WHERE job.MediaRequestId = child.Id)
                  AND EXISTS (
                      SELECT 1 FROM MediaRequests AS anchor
                      WHERE anchor.Id <> child.Id
                        AND anchor.MediaId = child.MediaId
                        AND anchor.MediaType = child.MediaType
                        AND COALESCE(anchor.RequestedByUserId, -1) = COALESCE(child.RequestedByUserId, -1)
                        AND anchor.MonitorMode <> 0
                        AND anchor.Status NOT IN (3, 7)
                  );
                """);

            migrationBuilder.CreateIndex(
                name: "IX_MediaRequests_MonitoringAnchorId_MonitoringSeasonNumber",
                table: "MediaRequests",
                columns: new[] { "MonitoringAnchorId", "MonitoringSeasonNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FulfillmentJobs_MediaRequestId",
                table: "FulfillmentJobs",
                column: "MediaRequestId",
                unique: true,
                filter: "\"Status\" IN (0, 1, 2, 7)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MediaRequests_MonitoringAnchorId_MonitoringSeasonNumber",
                table: "MediaRequests");

            migrationBuilder.DropIndex(
                name: "IX_FulfillmentJobs_MediaRequestId",
                table: "FulfillmentJobs");

            migrationBuilder.DropColumn(
                name: "MonitoringAnchorId",
                table: "MediaRequests");

            migrationBuilder.DropColumn(
                name: "MonitoringSeasonNumber",
                table: "MediaRequests");

            migrationBuilder.CreateIndex(
                name: "IX_FulfillmentJobs_MediaRequestId",
                table: "FulfillmentJobs",
                column: "MediaRequestId");
        }
    }
}

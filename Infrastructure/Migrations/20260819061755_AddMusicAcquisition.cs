using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PlexRequestsHosted.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddMusicAcquisition : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AcquisitionContextJson",
                table: "SearchTasks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExternalId",
                table: "SearchTasks",
                type: "TEXT",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExternalSource",
                table: "SearchTasks",
                type: "TEXT",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MediaKind",
                table: "SearchTasks",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "RequestScopeKind",
                table: "SearchTasks",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "AcquisitionContextJson",
                table: "FulfillmentJobs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MediaKind",
                table: "FulfillmentJobs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "RequestScopeKind",
                table: "FulfillmentJobs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            // Backfill the provider-neutral shape of jobs/tasks created before these fields existed.
            // MediaType: Movie=0, TV=1, Music=2, Anime=3. MediaKind: Movie=0, Series=1, Album=2.
            // RequestScope: Title=0, Series=1, Seasons=2, Episodes=3, Album=4.
            migrationBuilder.Sql("""
                UPDATE FulfillmentJobs
                SET MediaKind = CASE MediaType WHEN 1 THEN 1 WHEN 2 THEN 2 WHEN 3 THEN 1 ELSE 0 END,
                    RequestScopeKind = CASE
                        WHEN MediaType = 2 THEN 4
                        WHEN MediaType IN (1, 3) AND RequestedEpisodesCsv IS NOT NULL AND RequestedEpisodesCsv <> '' THEN 3
                        WHEN MediaType IN (1, 3) AND RequestedSeasonsCsv IS NOT NULL AND RequestedSeasonsCsv <> '' THEN 2
                        WHEN MediaType IN (1, 3) THEN 1
                        ELSE 0 END;

                UPDATE SearchTasks
                SET MediaKind = CASE MediaType WHEN 1 THEN 1 WHEN 2 THEN 2 WHEN 3 THEN 1 ELSE 0 END,
                    RequestScopeKind = CASE
                        WHEN MediaType = 2 THEN 4
                        WHEN MediaType IN (1, 3) AND Episode IS NOT NULL THEN 3
                        WHEN MediaType IN (1, 3) AND Season IS NOT NULL THEN 2
                        WHEN MediaType IN (1, 3) THEN 1
                        ELSE 0 END;
                """);

            // The general-purpose built-ins already search music safely; the module remains disabled until
            // import/Plex verification lands, so this only makes their capability truthful and ready.
            migrationBuilder.Sql("""
                UPDATE IndexerMediaCapabilities
                SET Enabled = 1, CategoriesCsv = COALESCE(NULLIF(CategoriesCsv, ''), '3000')
                WHERE MediaType = 2
                  AND IndexerId IN (
                      SELECT Id FROM Indexers
                      WHERE Implementation IN ('1337x', 'ext.to', 'PirateBay', 'TorrentsCSV'));
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AcquisitionContextJson",
                table: "SearchTasks");

            migrationBuilder.DropColumn(
                name: "ExternalId",
                table: "SearchTasks");

            migrationBuilder.DropColumn(
                name: "ExternalSource",
                table: "SearchTasks");

            migrationBuilder.DropColumn(
                name: "MediaKind",
                table: "SearchTasks");

            migrationBuilder.DropColumn(
                name: "RequestScopeKind",
                table: "SearchTasks");

            migrationBuilder.DropColumn(
                name: "AcquisitionContextJson",
                table: "FulfillmentJobs");

            migrationBuilder.DropColumn(
                name: "MediaKind",
                table: "FulfillmentJobs");

            migrationBuilder.DropColumn(
                name: "RequestScopeKind",
                table: "FulfillmentJobs");
        }
    }
}

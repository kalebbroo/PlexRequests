using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PlexRequestsHosted.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSeriesEpisodeOrdering : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SeriesEpisodeOrderProfilesJson",
                table: "LibraryOrganizationPreferences",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EpisodeOrderProfileJson",
                table: "FulfillmentJobs",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SeriesEpisodeOrderProfilesJson",
                table: "LibraryOrganizationPreferences");

            migrationBuilder.DropColumn(
                name: "EpisodeOrderProfileJson",
                table: "FulfillmentJobs");
        }
    }
}

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PlexRequestsHosted.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddEpisodeAndMonitorFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "Monitored",
                table: "MediaRequests",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "RequestedEpisodesCsv",
                table: "MediaRequests",
                type: "TEXT",
                maxLength: 4096,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RequestedEpisodesCsv",
                table: "FulfillmentJobs",
                type: "TEXT",
                maxLength: 4096,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Monitored",
                table: "MediaRequests");

            migrationBuilder.DropColumn(
                name: "RequestedEpisodesCsv",
                table: "MediaRequests");

            migrationBuilder.DropColumn(
                name: "RequestedEpisodesCsv",
                table: "FulfillmentJobs");
        }
    }
}

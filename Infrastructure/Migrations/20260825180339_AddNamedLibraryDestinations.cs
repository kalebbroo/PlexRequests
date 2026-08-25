using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PlexRequestsHosted.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddNamedLibraryDestinations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsAnime",
                table: "MediaRequests",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LibraryDestinationId",
                table: "MediaRequests",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LibraryDestinationsJson",
                table: "LibraryOrganizationPreferences",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LibraryDestinationId",
                table: "FulfillmentJobs",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LibraryDestinationKind",
                table: "FulfillmentJobs",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LibraryDestinationName",
                table: "FulfillmentJobs",
                type: "TEXT",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LibraryDestinationRootPath",
                table: "FulfillmentJobs",
                type: "TEXT",
                maxLength: 1024,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LibraryDestinationTemplate",
                table: "FulfillmentJobs",
                type: "TEXT",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LibraryPlexSectionId",
                table: "FulfillmentJobs",
                type: "TEXT",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LibrarySeasonPackFolderTemplate",
                table: "FulfillmentJobs",
                type: "TEXT",
                maxLength: 512,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsAnime",
                table: "MediaRequests");

            migrationBuilder.DropColumn(
                name: "LibraryDestinationId",
                table: "MediaRequests");

            migrationBuilder.DropColumn(
                name: "LibraryDestinationsJson",
                table: "LibraryOrganizationPreferences");

            migrationBuilder.DropColumn(
                name: "LibraryDestinationId",
                table: "FulfillmentJobs");

            migrationBuilder.DropColumn(
                name: "LibraryDestinationKind",
                table: "FulfillmentJobs");

            migrationBuilder.DropColumn(
                name: "LibraryDestinationName",
                table: "FulfillmentJobs");

            migrationBuilder.DropColumn(
                name: "LibraryDestinationRootPath",
                table: "FulfillmentJobs");

            migrationBuilder.DropColumn(
                name: "LibraryDestinationTemplate",
                table: "FulfillmentJobs");

            migrationBuilder.DropColumn(
                name: "LibraryPlexSectionId",
                table: "FulfillmentJobs");

            migrationBuilder.DropColumn(
                name: "LibrarySeasonPackFolderTemplate",
                table: "FulfillmentJobs");
        }
    }
}

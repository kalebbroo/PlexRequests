using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PlexRequestsHosted.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddExternalIdScaffold : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ExternalId",
                table: "MediaRequests",
                type: "TEXT",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExternalSource",
                table: "MediaRequests",
                type: "TEXT",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExternalId",
                table: "FulfillmentJobs",
                type: "TEXT",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExternalSource",
                table: "FulfillmentJobs",
                type: "TEXT",
                maxLength: 32,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ExternalId",
                table: "MediaRequests");

            migrationBuilder.DropColumn(
                name: "ExternalSource",
                table: "MediaRequests");

            migrationBuilder.DropColumn(
                name: "ExternalId",
                table: "FulfillmentJobs");

            migrationBuilder.DropColumn(
                name: "ExternalSource",
                table: "FulfillmentJobs");
        }
    }
}

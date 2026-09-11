using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PlexRequestsHosted.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPlexLibraryAdoption : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LibraryInventoryKey",
                table: "MediaRequests",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_MediaRequests_LibraryInventoryKey",
                table: "MediaRequests",
                column: "LibraryInventoryKey",
                unique: true,
                filter: "\"LibraryInventoryKey\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MediaRequests_LibraryInventoryKey",
                table: "MediaRequests");

            migrationBuilder.DropColumn(
                name: "LibraryInventoryKey",
                table: "MediaRequests");
        }
    }
}

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PlexRequestsHosted.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddFulfillmentJobMediaIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_FulfillmentJobs_MediaId_MediaType",
                table: "FulfillmentJobs",
                columns: new[] { "MediaId", "MediaType" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_FulfillmentJobs_MediaId_MediaType",
                table: "FulfillmentJobs");
        }
    }
}

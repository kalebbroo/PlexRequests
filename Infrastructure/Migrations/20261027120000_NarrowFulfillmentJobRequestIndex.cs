using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using PlexRequestsHosted.Infrastructure.Data;

#nullable disable

namespace PlexRequestsHosted.Infrastructure.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(AppDbContext))]
    [Migration("20261027120000_NarrowFulfillmentJobRequestIndex")]
    public partial class NarrowFulfillmentJobRequestIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // A Deferred job is parked, not in flight, so it no longer holds its request's single slot.
            migrationBuilder.DropIndex(
                name: "IX_FulfillmentJobs_MediaRequestId",
                table: "FulfillmentJobs");

            migrationBuilder.CreateIndex(
                name: "IX_FulfillmentJobs_MediaRequestId",
                table: "FulfillmentJobs",
                column: "MediaRequestId",
                unique: true,
                filter: "\"Status\" IN (0, 1, 2)");

            migrationBuilder.AddColumn<int>(
                name: "ReplacedFileHeight",
                table: "FulfillmentJobs",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ReplacedFileHeight",
                table: "FulfillmentJobs");

            migrationBuilder.DropIndex(
                name: "IX_FulfillmentJobs_MediaRequestId",
                table: "FulfillmentJobs");

            migrationBuilder.CreateIndex(
                name: "IX_FulfillmentJobs_MediaRequestId",
                table: "FulfillmentJobs",
                column: "MediaRequestId",
                unique: true,
                filter: "\"Status\" IN (0, 1, 2, 7)");
        }
    }
}

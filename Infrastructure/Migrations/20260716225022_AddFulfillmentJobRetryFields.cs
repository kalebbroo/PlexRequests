using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PlexRequestsHosted.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddFulfillmentJobRetryFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "DeferCount",
                table: "FulfillmentJobs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "NextRetryAt",
                table: "FulfillmentJobs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ReleaseDate",
                table: "FulfillmentJobs",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DeferCount",
                table: "FulfillmentJobs");

            migrationBuilder.DropColumn(
                name: "NextRetryAt",
                table: "FulfillmentJobs");

            migrationBuilder.DropColumn(
                name: "ReleaseDate",
                table: "FulfillmentJobs");
        }
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PlexRequestsHosted.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddFulfillmentJobs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "FulfillmentJobs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    MediaRequestId = table.Column<int>(type: "INTEGER", nullable: false),
                    MediaId = table.Column<int>(type: "INTEGER", nullable: false),
                    MediaType = table.Column<int>(type: "INTEGER", nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    Year = table.Column<int>(type: "INTEGER", nullable: true),
                    TmdbId = table.Column<int>(type: "INTEGER", nullable: true),
                    ImdbId = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    TvdbId = table.Column<int>(type: "INTEGER", nullable: true),
                    RequestedSeasonsCsv = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    Quality = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    Attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    Progress = table.Column<int>(type: "INTEGER", nullable: false),
                    ClaimedBy = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    LastError = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ClaimedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FulfillmentJobs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FulfillmentJobs_MediaRequests_MediaRequestId",
                        column: x => x.MediaRequestId,
                        principalTable: "MediaRequests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FulfillmentJobs_MediaRequestId",
                table: "FulfillmentJobs",
                column: "MediaRequestId");

            migrationBuilder.CreateIndex(
                name: "IX_FulfillmentJobs_Status",
                table: "FulfillmentJobs",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FulfillmentJobs");
        }
    }
}

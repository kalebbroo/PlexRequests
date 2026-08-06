using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PlexRequestsHosted.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddInteractiveSearchAndBlocklist : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ForcedIndexerId",
                table: "FulfillmentJobs",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ForcedMagnet",
                table: "FulfillmentJobs",
                type: "TEXT",
                maxLength: 2048,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ForcedReleaseName",
                table: "FulfillmentJobs",
                type: "TEXT",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsManualGrab",
                table: "FulfillmentJobs",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "ReleaseBlocklist",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    InfoHash = table.Column<string>(type: "TEXT", maxLength: 40, nullable: true),
                    NormalizedReleaseName = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    Scope = table.Column<int>(type: "INTEGER", nullable: false),
                    MediaRequestId = table.Column<int>(type: "INTEGER", nullable: true),
                    MediaId = table.Column<int>(type: "INTEGER", nullable: false),
                    MediaType = table.Column<int>(type: "INTEGER", nullable: false),
                    Season = table.Column<int>(type: "INTEGER", nullable: true),
                    Episode = table.Column<int>(type: "INTEGER", nullable: true),
                    IndexerId = table.Column<int>(type: "INTEGER", nullable: true),
                    Reason = table.Column<int>(type: "INTEGER", nullable: false),
                    Detail = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    ReleaseName = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    BlockedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    BlockedByUserId = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReleaseBlocklist", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SearchTasks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    RequestedByUserId = table.Column<int>(type: "INTEGER", nullable: true),
                    MediaRequestId = table.Column<int>(type: "INTEGER", nullable: true),
                    MediaType = table.Column<int>(type: "INTEGER", nullable: false),
                    MediaId = table.Column<int>(type: "INTEGER", nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    Year = table.Column<int>(type: "INTEGER", nullable: true),
                    ImdbId = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    Season = table.Column<int>(type: "INTEGER", nullable: true),
                    Episode = table.Column<int>(type: "INTEGER", nullable: true),
                    QualityProfileId = table.Column<int>(type: "INTEGER", nullable: true),
                    IndexerId = table.Column<int>(type: "INTEGER", nullable: true),
                    IncludeRejected = table.Column<bool>(type: "INTEGER", nullable: false),
                    ClaimedBy = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    ClaimedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ResultsJson = table.Column<string>(type: "TEXT", nullable: true),
                    IndexerOutcomesJson = table.Column<string>(type: "TEXT", nullable: true),
                    Error = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SearchTasks", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ReleaseBlocklist_InfoHash",
                table: "ReleaseBlocklist",
                column: "InfoHash");

            migrationBuilder.CreateIndex(
                name: "IX_ReleaseBlocklist_InfoHash_MediaRequestId",
                table: "ReleaseBlocklist",
                columns: new[] { "InfoHash", "MediaRequestId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ReleaseBlocklist_MediaId_MediaType",
                table: "ReleaseBlocklist",
                columns: new[] { "MediaId", "MediaType" });

            migrationBuilder.CreateIndex(
                name: "IX_ReleaseBlocklist_NormalizedReleaseName",
                table: "ReleaseBlocklist",
                column: "NormalizedReleaseName");

            migrationBuilder.CreateIndex(
                name: "IX_SearchTasks_ExpiresAt",
                table: "SearchTasks",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_SearchTasks_MediaRequestId",
                table: "SearchTasks",
                column: "MediaRequestId");

            migrationBuilder.CreateIndex(
                name: "IX_SearchTasks_Status_CreatedAt",
                table: "SearchTasks",
                columns: new[] { "Status", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ReleaseBlocklist");

            migrationBuilder.DropTable(
                name: "SearchTasks");

            migrationBuilder.DropColumn(
                name: "ForcedIndexerId",
                table: "FulfillmentJobs");

            migrationBuilder.DropColumn(
                name: "ForcedMagnet",
                table: "FulfillmentJobs");

            migrationBuilder.DropColumn(
                name: "ForcedReleaseName",
                table: "FulfillmentJobs");

            migrationBuilder.DropColumn(
                name: "IsManualGrab",
                table: "FulfillmentJobs");
        }
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PlexRequestsHosted.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddFulfillmentTorrentTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "FulfillmentTorrents",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    FulfillmentJobId = table.Column<int>(type: "INTEGER", nullable: false),
                    TorrentId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    InfoHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    ReleaseName = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    Season = table.Column<int>(type: "INTEGER", nullable: true),
                    Episode = table.Column<int>(type: "INTEGER", nullable: true),
                    IsPack = table.Column<bool>(type: "INTEGER", nullable: false),
                    NeededEpisodesCsv = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    Resolution = table.Column<int>(type: "INTEGER", nullable: false),
                    State = table.Column<int>(type: "INTEGER", nullable: false),
                    Progress = table.Column<double>(type: "REAL", nullable: false),
                    Seeds = table.Column<int>(type: "INTEGER", nullable: false),
                    Peers = table.Column<int>(type: "INTEGER", nullable: false),
                    DownloadRateBytesPerSec = table.Column<double>(type: "REAL", nullable: false),
                    TotalSizeBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    ProgressChangedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastSeenAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    TrackerStatus = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    FailReason = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    AddedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ImportedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FulfillmentTorrents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FulfillmentTorrents_FulfillmentJobs_FulfillmentJobId",
                        column: x => x.FulfillmentJobId,
                        principalTable: "FulfillmentJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FulfillmentTorrents_FulfillmentJobId_TorrentId",
                table: "FulfillmentTorrents",
                columns: new[] { "FulfillmentJobId", "TorrentId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FulfillmentTorrents_InfoHash",
                table: "FulfillmentTorrents",
                column: "InfoHash");

            migrationBuilder.CreateIndex(
                name: "IX_FulfillmentTorrents_State",
                table: "FulfillmentTorrents",
                column: "State");

            migrationBuilder.CreateIndex(
                name: "IX_FulfillmentTorrents_TorrentId",
                table: "FulfillmentTorrents",
                column: "TorrentId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FulfillmentTorrents");
        }
    }
}

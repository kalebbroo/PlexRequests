using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PlexRequestsHosted.Infrastructure.Catalog.Migrations
{
    /// <inheritdoc />
    public partial class InitialCatalog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BatchReceipts",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    IndexerId = table.Column<int>(type: "INTEGER", nullable: false),
                    BatchId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    ItemCount = table.Column<int>(type: "INTEGER", nullable: false),
                    CommittedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BatchReceipts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Checkpoints",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    IndexerId = table.Column<int>(type: "INTEGER", nullable: false),
                    Source = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Cursor = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                    LastBatchId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    LastAttemptAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastSuccessAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    NextAttemptAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ConsecutiveFailures = table.Column<int>(type: "INTEGER", nullable: false),
                    CircuitState = table.Column<int>(type: "INTEGER", nullable: false),
                    LastError = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    TotalItemsSeen = table.Column<long>(type: "INTEGER", nullable: false),
                    TotalItemsResolved = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Checkpoints", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Releases",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    InfoHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ReleaseName = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    NormalizedTitle = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    MagnetUri = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: true),
                    ImdbId = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    SizeBytes = table.Column<long>(type: "INTEGER", nullable: true),
                    PublishedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    FirstSeenAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastSeenAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ParserVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    MediaType = table.Column<int>(type: "INTEGER", nullable: true),
                    Year = table.Column<int>(type: "INTEGER", nullable: true),
                    Season = table.Column<int>(type: "INTEGER", nullable: true),
                    SeasonEnd = table.Column<int>(type: "INTEGER", nullable: true),
                    Episode = table.Column<int>(type: "INTEGER", nullable: true),
                    EpisodeStart = table.Column<int>(type: "INTEGER", nullable: true),
                    EpisodeEnd = table.Column<int>(type: "INTEGER", nullable: true),
                    IsSeasonPack = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsCompleteSeries = table.Column<bool>(type: "INTEGER", nullable: false),
                    Resolution = table.Column<int>(type: "INTEGER", nullable: false),
                    ReleaseSource = table.Column<int>(type: "INTEGER", nullable: false),
                    Codec = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Releases", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Sightings",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ReleaseId = table.Column<long>(type: "INTEGER", nullable: true),
                    IndexerId = table.Column<int>(type: "INTEGER", nullable: false),
                    Source = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    ExternalId = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    SourceUrl = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                    ReleaseName = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    Category = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    Uploader = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    Seeders = table.Column<int>(type: "INTEGER", nullable: true),
                    Leechers = table.Column<int>(type: "INTEGER", nullable: true),
                    SizeBytes = table.Column<long>(type: "INTEGER", nullable: true),
                    PublishedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    FirstSeenAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastSeenAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastHydratedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    HydrationState = table.Column<int>(type: "INTEGER", nullable: false),
                    HydrationError = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Sightings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Sightings_Releases_ReleaseId",
                        column: x => x.ReleaseId,
                        principalTable: "Releases",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BatchReceipts_CommittedAt",
                table: "BatchReceipts",
                column: "CommittedAt");

            migrationBuilder.CreateIndex(
                name: "IX_BatchReceipts_IndexerId_BatchId",
                table: "BatchReceipts",
                columns: new[] { "IndexerId", "BatchId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Checkpoints_IndexerId",
                table: "Checkpoints",
                column: "IndexerId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Checkpoints_NextAttemptAt",
                table: "Checkpoints",
                column: "NextAttemptAt");

            migrationBuilder.CreateIndex(
                name: "IX_Releases_InfoHash",
                table: "Releases",
                column: "InfoHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Releases_LastSeenAt",
                table: "Releases",
                column: "LastSeenAt");

            migrationBuilder.CreateIndex(
                name: "IX_Releases_NormalizedTitle",
                table: "Releases",
                column: "NormalizedTitle");

            migrationBuilder.CreateIndex(
                name: "IX_Releases_Season_Episode",
                table: "Releases",
                columns: new[] { "Season", "Episode" });

            migrationBuilder.CreateIndex(
                name: "IX_Sightings_HydrationState",
                table: "Sightings",
                column: "HydrationState");

            migrationBuilder.CreateIndex(
                name: "IX_Sightings_IndexerId_ExternalId",
                table: "Sightings",
                columns: new[] { "IndexerId", "ExternalId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Sightings_LastSeenAt",
                table: "Sightings",
                column: "LastSeenAt");

            migrationBuilder.CreateIndex(
                name: "IX_Sightings_ReleaseId",
                table: "Sightings",
                column: "ReleaseId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BatchReceipts");

            migrationBuilder.DropTable(
                name: "Checkpoints");

            migrationBuilder.DropTable(
                name: "Sightings");

            migrationBuilder.DropTable(
                name: "Releases");
        }
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PlexRequestsHosted.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddLibraryOrganizationAndImportAudit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ImportedFiles",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    FulfillmentJobId = table.Column<int>(type: "INTEGER", nullable: false),
                    TorrentId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    SourcePath = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                    DestinationPath = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                    FileType = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    SeasonNumber = table.Column<int>(type: "INTEGER", nullable: true),
                    EpisodeNumber = table.Column<int>(type: "INTEGER", nullable: true),
                    SizeBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    ImportedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ImportedFiles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ImportedFiles_FulfillmentJobs_FulfillmentJobId",
                        column: x => x.FulfillmentJobId,
                        principalTable: "FulfillmentJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "LibraryOrganizationPreferences",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    IsSingleton = table.Column<bool>(type: "INTEGER", nullable: false),
                    MoviePath = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    TvPath = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    MovieTemplate = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    TvEpisodeTemplate = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    SeasonPackFolderTemplate = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    LibraryRootRulesJson = table.Column<string>(type: "TEXT", nullable: true),
                    TransferMode = table.Column<int>(type: "INTEGER", nullable: false),
                    ExtractArchives = table.Column<bool>(type: "INTEGER", nullable: false),
                    SplitSeasonPacks = table.Column<bool>(type: "INTEGER", nullable: false),
                    KeepSubtitles = table.Column<bool>(type: "INTEGER", nullable: false),
                    SubtitleExtensionsCsv = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    VideoExtensionsCsv = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    MinVideoFileSizeMb = table.Column<double>(type: "REAL", nullable: false),
                    DeleteSourceAfterImport = table.Column<bool>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LibraryOrganizationPreferences", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ImportedFiles_FulfillmentJobId",
                table: "ImportedFiles",
                column: "FulfillmentJobId");

            migrationBuilder.CreateIndex(
                name: "IX_LibraryOrganizationPreferences_IsSingleton",
                table: "LibraryOrganizationPreferences",
                column: "IsSingleton",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ImportedFiles");

            migrationBuilder.DropTable(
                name: "LibraryOrganizationPreferences");
        }
    }
}

using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using PlexRequestsHosted.Infrastructure.Data;

#nullable disable

namespace PlexRequestsHosted.Infrastructure.Migrations
{
    [DbContext(typeof(AppDbContext))]
    [Migration("20260909232804_AddPlexLibraryFileInventory")]
    public partial class AddPlexLibraryFileInventory : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PlexLibraryFiles",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SectionKey = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    SectionTitle = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    RatingKey = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ShowRatingKey = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    PlexPartKey = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    FilePath = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                    MediaType = table.Column<int>(type: "INTEGER", nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    Year = table.Column<int>(type: "INTEGER", nullable: true),
                    SeasonNumber = table.Column<int>(type: "INTEGER", nullable: true),
                    EpisodeNumber = table.Column<int>(type: "INTEGER", nullable: true),
                    SizeBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    ResolutionHeight = table.Column<int>(type: "INTEGER", nullable: false),
                    VideoCodec = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    AudioCodec = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    LastSeenAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    MissedScans = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table => table.PrimaryKey("PK_PlexLibraryFiles", x => x.Id));

            migrationBuilder.CreateIndex(
                name: "IX_PlexLibraryFiles_FilePath",
                table: "PlexLibraryFiles",
                column: "FilePath");

            migrationBuilder.CreateIndex(
                name: "IX_PlexLibraryFiles_LastSeenAt",
                table: "PlexLibraryFiles",
                column: "LastSeenAt");

            migrationBuilder.CreateIndex(
                name: "IX_PlexLibraryFiles_SectionKey_RatingKey_PlexPartKey",
                table: "PlexLibraryFiles",
                columns: new[] { "SectionKey", "RatingKey", "PlexPartKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PlexLibraryFiles_ShowRatingKey",
                table: "PlexLibraryFiles",
                column: "ShowRatingKey");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "PlexLibraryFiles");
        }
    }
}

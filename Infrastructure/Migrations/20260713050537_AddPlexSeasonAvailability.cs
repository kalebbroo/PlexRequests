using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PlexRequestsHosted.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPlexSeasonAvailability : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PlexSeasonAvailability",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ShowRatingKey = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    SeasonNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    AvailableEpisodesCsv = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: false),
                    EpisodeCount = table.Column<int>(type: "INTEGER", nullable: false),
                    LastSeenAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlexSeasonAvailability", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PlexSeasonAvailability_ShowRatingKey",
                table: "PlexSeasonAvailability",
                column: "ShowRatingKey");

            migrationBuilder.CreateIndex(
                name: "IX_PlexSeasonAvailability_ShowRatingKey_SeasonNumber",
                table: "PlexSeasonAvailability",
                columns: new[] { "ShowRatingKey", "SeasonNumber" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PlexSeasonAvailability");
        }
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PlexRequestsHosted.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDownloadPreferencesAndSeasonTargets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SeasonTargetsJson",
                table: "FulfillmentJobs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "DownloadPreferences",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    IsSingleton = table.Column<bool>(type: "INTEGER", nullable: false),
                    SeasonPackStrategy = table.Column<int>(type: "INTEGER", nullable: false),
                    AllowEpisodeFallback = table.Column<bool>(type: "INTEGER", nullable: false),
                    MaxEpisodesForFanout = table.Column<int>(type: "INTEGER", nullable: false),
                    MinSeeders = table.Column<int>(type: "INTEGER", nullable: false),
                    MaxSizeGb = table.Column<double>(type: "REAL", nullable: false),
                    MaxSeasonPackSizeGb = table.Column<double>(type: "REAL", nullable: false),
                    PreferredGroupsCsv = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    PreferX265 = table.Column<bool>(type: "INTEGER", nullable: false),
                    PreferHdr = table.Column<bool>(type: "INTEGER", nullable: false),
                    PreferHigherQualitySource = table.Column<bool>(type: "INTEGER", nullable: false),
                    EnforceQualityFloor = table.Column<bool>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DownloadPreferences", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DownloadPreferences_IsSingleton",
                table: "DownloadPreferences",
                column: "IsSingleton",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DownloadPreferences");

            migrationBuilder.DropColumn(
                name: "SeasonTargetsJson",
                table: "FulfillmentJobs");
        }
    }
}

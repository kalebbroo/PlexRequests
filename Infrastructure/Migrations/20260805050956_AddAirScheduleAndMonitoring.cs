using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PlexRequestsHosted.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAirScheduleAndMonitoring : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AirTimeOverrideMinutes",
                table: "MediaRequests",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "AutoMonitorNewSeasons",
                table: "MediaRequests",
                type: "INTEGER",
                nullable: false,
                // Matches the entity default. EF's false would mean an already-monitored series silently
                // stops picking up new seasons — the exact behaviour people expect from "monitor this show".
                defaultValue: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastMonitorCheckAt",
                table: "MediaRequests",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MonitorMode",
                table: "MediaRequests",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "MonitoredSince",
                table: "MediaRequests",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "SearchForCutoffUpgrades",
                table: "MediaRequests",
                type: "INTEGER",
                nullable: false,
                // EF's false would switch quality upgrades off for every existing request, which is a
                // regression: today the upgrade scan considers them all.
                defaultValue: true);

            migrationBuilder.CreateTable(
                name: "AirSchedule",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ShowTmdbId = table.Column<int>(type: "INTEGER", nullable: false),
                    SeasonNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    EpisodeNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    EpisodeTitle = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    AirDate = table.Column<DateTime>(type: "TEXT", nullable: true),
                    AirsAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    MediaRequestId = table.Column<int>(type: "INTEGER", nullable: true),
                    Monitored = table.Column<bool>(type: "INTEGER", nullable: false),
                    HasFile = table.Column<bool>(type: "INTEGER", nullable: false),
                    SearchState = table.Column<int>(type: "INTEGER", nullable: false),
                    NextSearchAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastSearchedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    SearchAttempts = table.Column<int>(type: "INTEGER", nullable: false),
                    SourceProvider = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AirSchedule", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MonitoringPreferences",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    IsSingleton = table.Column<bool>(type: "INTEGER", nullable: false),
                    AssumedAirTimeLocal = table.Column<string>(type: "TEXT", maxLength: 8, nullable: false),
                    AirTimeZoneId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    PostAirDelayMinutes = table.Column<int>(type: "INTEGER", nullable: false),
                    RssSweepIntervalSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                    MaxSearchAttemptsPerEpisode = table.Column<int>(type: "INTEGER", nullable: false),
                    CalendarHorizonDays = table.Column<int>(type: "INTEGER", nullable: false),
                    CalendarBackfillDays = table.Column<int>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MonitoringPreferences", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MediaRequests_Monitored_MediaType_Status",
                table: "MediaRequests",
                columns: new[] { "Monitored", "MediaType", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_AirSchedule_MediaRequestId",
                table: "AirSchedule",
                column: "MediaRequestId");

            migrationBuilder.CreateIndex(
                name: "IX_AirSchedule_Monitored_SearchState",
                table: "AirSchedule",
                columns: new[] { "Monitored", "SearchState" });

            migrationBuilder.CreateIndex(
                name: "IX_AirSchedule_NextSearchAt",
                table: "AirSchedule",
                column: "NextSearchAt");

            migrationBuilder.CreateIndex(
                name: "IX_AirSchedule_ShowTmdbId_SeasonNumber_EpisodeNumber",
                table: "AirSchedule",
                columns: new[] { "ShowTmdbId", "SeasonNumber", "EpisodeNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MonitoringPreferences_IsSingleton",
                table: "MonitoringPreferences",
                column: "IsSingleton",
                unique: true);

            BackfillMonitorMode(migrationBuilder);
        }

        /// <inheritdoc />
        private static void BackfillMonitorMode(MigrationBuilder migrationBuilder)
        {
            // Carry the old boolean forward: a monitored series meant "keep fetching everything".
            //   0 = None, 1 = AllEpisodes  (Shared.Enums.MonitorMode)
            migrationBuilder.Sql(
                "UPDATE MediaRequests SET MonitorMode = CASE WHEN Monitored = 1 THEN 1 ELSE 0 END;");
            migrationBuilder.Sql(
                "UPDATE MediaRequests SET MonitoredSince = RequestedAt WHERE Monitored = 1;");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AirSchedule");

            migrationBuilder.DropTable(
                name: "MonitoringPreferences");

            migrationBuilder.DropIndex(
                name: "IX_MediaRequests_Monitored_MediaType_Status",
                table: "MediaRequests");

            migrationBuilder.DropColumn(
                name: "AirTimeOverrideMinutes",
                table: "MediaRequests");

            migrationBuilder.DropColumn(
                name: "AutoMonitorNewSeasons",
                table: "MediaRequests");

            migrationBuilder.DropColumn(
                name: "LastMonitorCheckAt",
                table: "MediaRequests");

            migrationBuilder.DropColumn(
                name: "MonitorMode",
                table: "MediaRequests");

            migrationBuilder.DropColumn(
                name: "MonitoredSince",
                table: "MediaRequests");

            migrationBuilder.DropColumn(
                name: "SearchForCutoffUpgrades",
                table: "MediaRequests");
        }
    }
}

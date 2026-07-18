using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PlexRequestsHosted.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddJobsAndUpgradeTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AchievedQuality",
                table: "MediaRequests",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "CutoffMet",
                table: "MediaRequests",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastUpgradeSearchAt",
                table: "MediaRequests",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "UpgradeAttempts",
                table: "MediaRequests",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "ResolutionHeight",
                table: "ImportedFiles",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "Escalated",
                table: "FulfillmentJobs",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsUpgrade",
                table: "FulfillmentJobs",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "ReplacePathsJson",
                table: "FulfillmentJobs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "JobRuns",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    JobType = table.Column<int>(type: "INTEGER", nullable: false),
                    ScheduledJobId = table.Column<int>(type: "INTEGER", nullable: true),
                    StartedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    FinishedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    ItemsProcessed = table.Column<int>(type: "INTEGER", nullable: false),
                    Message = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    TriggeredManually = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JobRuns", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ScheduledJobs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    JobType = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    IntervalSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                    NextRunAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastRunAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastStatus = table.Column<int>(type: "INTEGER", nullable: true),
                    LastMessage = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    IsRunning = table.Column<bool>(type: "INTEGER", nullable: false),
                    ManualRunRequested = table.Column<bool>(type: "INTEGER", nullable: false),
                    PayloadJson = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScheduledJobs", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FulfillmentJobs_NextRetryAt",
                table: "FulfillmentJobs",
                column: "NextRetryAt");

            migrationBuilder.CreateIndex(
                name: "IX_JobRuns_JobType",
                table: "JobRuns",
                column: "JobType");

            migrationBuilder.CreateIndex(
                name: "IX_JobRuns_StartedAt",
                table: "JobRuns",
                column: "StartedAt");

            migrationBuilder.CreateIndex(
                name: "IX_ScheduledJobs_JobType",
                table: "ScheduledJobs",
                column: "JobType",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ScheduledJobs_NextRunAt",
                table: "ScheduledJobs",
                column: "NextRunAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "JobRuns");

            migrationBuilder.DropTable(
                name: "ScheduledJobs");

            migrationBuilder.DropIndex(
                name: "IX_FulfillmentJobs_NextRetryAt",
                table: "FulfillmentJobs");

            migrationBuilder.DropColumn(
                name: "AchievedQuality",
                table: "MediaRequests");

            migrationBuilder.DropColumn(
                name: "CutoffMet",
                table: "MediaRequests");

            migrationBuilder.DropColumn(
                name: "LastUpgradeSearchAt",
                table: "MediaRequests");

            migrationBuilder.DropColumn(
                name: "UpgradeAttempts",
                table: "MediaRequests");

            migrationBuilder.DropColumn(
                name: "ResolutionHeight",
                table: "ImportedFiles");

            migrationBuilder.DropColumn(
                name: "Escalated",
                table: "FulfillmentJobs");

            migrationBuilder.DropColumn(
                name: "IsUpgrade",
                table: "FulfillmentJobs");

            migrationBuilder.DropColumn(
                name: "ReplacePathsJson",
                table: "FulfillmentJobs");
        }
    }
}

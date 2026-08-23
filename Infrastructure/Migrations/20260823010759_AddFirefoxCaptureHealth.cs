using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PlexRequestsHosted.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddFirefoxCaptureHealth : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AttentionDetails",
                table: "FirefoxCaptureDevices",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "CaptureEnabled",
                table: "FirefoxCaptureDevices",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "FailedUploads",
                table: "FirefoxCaptureDevices",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "HydrationPausedUntil",
                table: "FirefoxCaptureDevices",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastHeartbeatAt",
                table: "FirefoxCaptureDevices",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PendingDetails",
                table: "FirefoxCaptureDevices",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "QueuedUploads",
                table: "FirefoxCaptureDevices",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AttentionDetails",
                table: "FirefoxCaptureDevices");

            migrationBuilder.DropColumn(
                name: "CaptureEnabled",
                table: "FirefoxCaptureDevices");

            migrationBuilder.DropColumn(
                name: "FailedUploads",
                table: "FirefoxCaptureDevices");

            migrationBuilder.DropColumn(
                name: "HydrationPausedUntil",
                table: "FirefoxCaptureDevices");

            migrationBuilder.DropColumn(
                name: "LastHeartbeatAt",
                table: "FirefoxCaptureDevices");

            migrationBuilder.DropColumn(
                name: "PendingDetails",
                table: "FirefoxCaptureDevices");

            migrationBuilder.DropColumn(
                name: "QueuedUploads",
                table: "FirefoxCaptureDevices");
        }
    }
}

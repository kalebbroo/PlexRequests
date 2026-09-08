using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PlexRequestsHosted.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddStorageSafetyGuardrails : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AutoCleanupStaleArtifacts",
                table: "LibraryOrganizationPreferences",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<double>(
                name: "MinimumFreeSpaceGb",
                table: "LibraryOrganizationPreferences",
                type: "REAL",
                nullable: false,
                defaultValue: 20.0);

            migrationBuilder.AddColumn<int>(
                name: "StaleArtifactHours",
                table: "LibraryOrganizationPreferences",
                type: "INTEGER",
                nullable: false,
                defaultValue: 6);

            migrationBuilder.AddColumn<int>(
                name: "TemporaryHeadroomPercent",
                table: "LibraryOrganizationPreferences",
                type: "INTEGER",
                nullable: false,
                defaultValue: 20);

            migrationBuilder.AddColumn<DateTime>(
                name: "CleanupCompletedAt",
                table: "FulfillmentTorrents",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CleanupError",
                table: "FulfillmentTorrents",
                type: "TEXT",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "CleanupLastAttemptAt",
                table: "FulfillmentTorrents",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AutoCleanupStaleArtifacts",
                table: "LibraryOrganizationPreferences");

            migrationBuilder.DropColumn(
                name: "MinimumFreeSpaceGb",
                table: "LibraryOrganizationPreferences");

            migrationBuilder.DropColumn(
                name: "StaleArtifactHours",
                table: "LibraryOrganizationPreferences");

            migrationBuilder.DropColumn(
                name: "TemporaryHeadroomPercent",
                table: "LibraryOrganizationPreferences");

            migrationBuilder.DropColumn(
                name: "CleanupCompletedAt",
                table: "FulfillmentTorrents");

            migrationBuilder.DropColumn(
                name: "CleanupError",
                table: "FulfillmentTorrents");

            migrationBuilder.DropColumn(
                name: "CleanupLastAttemptAt",
                table: "FulfillmentTorrents");
        }
    }
}

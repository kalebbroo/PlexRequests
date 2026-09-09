using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PlexRequestsHosted.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddMediaMetadataScans : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "MediaMetadataScanAttempts",
                table: "ImportedFiles",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "MediaMetadataScanClaimedAt",
                table: "ImportedFiles",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MediaMetadataScanClaimedBy",
                table: "ImportedFiles",
                type: "TEXT",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "MediaMetadataScanCompletedAt",
                table: "ImportedFiles",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MediaMetadataScanDetail",
                table: "ImportedFiles",
                type: "TEXT",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "MediaMetadataScanRequestedAt",
                table: "ImportedFiles",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MediaMetadataScanStatus",
                table: "ImportedFiles",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_ImportedFiles_MediaMetadataScanStatus_MediaMetadataScanClaimedAt",
                table: "ImportedFiles",
                columns: new[] { "MediaMetadataScanStatus", "MediaMetadataScanClaimedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ImportedFiles_MediaMetadataScanStatus_MediaMetadataScanClaimedAt",
                table: "ImportedFiles");

            migrationBuilder.DropColumn(
                name: "MediaMetadataScanAttempts",
                table: "ImportedFiles");

            migrationBuilder.DropColumn(
                name: "MediaMetadataScanClaimedAt",
                table: "ImportedFiles");

            migrationBuilder.DropColumn(
                name: "MediaMetadataScanClaimedBy",
                table: "ImportedFiles");

            migrationBuilder.DropColumn(
                name: "MediaMetadataScanCompletedAt",
                table: "ImportedFiles");

            migrationBuilder.DropColumn(
                name: "MediaMetadataScanDetail",
                table: "ImportedFiles");

            migrationBuilder.DropColumn(
                name: "MediaMetadataScanRequestedAt",
                table: "ImportedFiles");

            migrationBuilder.DropColumn(
                name: "MediaMetadataScanStatus",
                table: "ImportedFiles");
        }
    }
}

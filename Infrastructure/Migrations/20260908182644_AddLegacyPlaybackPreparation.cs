using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PlexRequestsHosted.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddLegacyPlaybackPreparation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "PlaybackPreparationAttempts",
                table: "ImportedFiles",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "PlaybackPreparationClaimedAt",
                table: "ImportedFiles",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PlaybackPreparationClaimedBy",
                table: "ImportedFiles",
                type: "TEXT",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PlaybackPreparationDetail",
                table: "ImportedFiles",
                type: "TEXT",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "PlaybackPreparedAt",
                table: "ImportedFiles",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ImportedFiles_PlaybackPreparedAt_PlaybackPreparationAttempts_PlaybackPreparationClaimedAt",
                table: "ImportedFiles",
                columns: new[] { "PlaybackPreparedAt", "PlaybackPreparationAttempts", "PlaybackPreparationClaimedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ImportedFiles_PlaybackPreparedAt_PlaybackPreparationAttempts_PlaybackPreparationClaimedAt",
                table: "ImportedFiles");

            migrationBuilder.DropColumn(
                name: "PlaybackPreparationAttempts",
                table: "ImportedFiles");

            migrationBuilder.DropColumn(
                name: "PlaybackPreparationClaimedAt",
                table: "ImportedFiles");

            migrationBuilder.DropColumn(
                name: "PlaybackPreparationClaimedBy",
                table: "ImportedFiles");

            migrationBuilder.DropColumn(
                name: "PlaybackPreparationDetail",
                table: "ImportedFiles");

            migrationBuilder.DropColumn(
                name: "PlaybackPreparedAt",
                table: "ImportedFiles");
        }
    }
}

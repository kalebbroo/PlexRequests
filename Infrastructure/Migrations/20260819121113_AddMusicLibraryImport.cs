using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PlexRequestsHosted.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddMusicLibraryImport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "RequestsEnabled",
                table: "MusicSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "AudioExtensionsCsv",
                table: "LibraryOrganizationPreferences",
                type: "TEXT",
                maxLength: 256,
                nullable: false,
                defaultValue: ".flac,.mp3,.m4a,.aac,.ogg,.opus,.wav,.wma,.alac");

            migrationBuilder.AddColumn<double>(
                name: "MinAudioFileSizeMb",
                table: "LibraryOrganizationPreferences",
                type: "REAL",
                nullable: false,
                defaultValue: 1.0);

            migrationBuilder.AddColumn<string>(
                name: "MusicPath",
                table: "LibraryOrganizationPreferences",
                type: "TEXT",
                maxLength: 1024,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "MusicTrackTemplate",
                table: "LibraryOrganizationPreferences",
                type: "TEXT",
                maxLength: 512,
                nullable: false,
                defaultValue: "{Artist}/{Album} ({Year})/{Disc:00}-{Track:00} - {TrackTitle}{Ext}");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RequestsEnabled",
                table: "MusicSettings");

            migrationBuilder.DropColumn(
                name: "AudioExtensionsCsv",
                table: "LibraryOrganizationPreferences");

            migrationBuilder.DropColumn(
                name: "MinAudioFileSizeMb",
                table: "LibraryOrganizationPreferences");

            migrationBuilder.DropColumn(
                name: "MusicPath",
                table: "LibraryOrganizationPreferences");

            migrationBuilder.DropColumn(
                name: "MusicTrackTemplate",
                table: "LibraryOrganizationPreferences");
        }
    }
}

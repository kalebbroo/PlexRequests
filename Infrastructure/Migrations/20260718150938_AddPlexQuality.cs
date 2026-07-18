using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PlexRequestsHosted.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPlexQuality : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "EpisodeQualityJson",
                table: "PlexSeasonAvailability",
                type: "TEXT",
                maxLength: 8192,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AudioCodec",
                table: "PlexMappings",
                type: "TEXT",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Bitrate",
                table: "PlexMappings",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "FileSizeBytes",
                table: "PlexMappings",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "VersionCount",
                table: "PlexMappings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "VideoCodec",
                table: "PlexMappings",
                type: "TEXT",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VideoResolution",
                table: "PlexMappings",
                type: "TEXT",
                maxLength: 16,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EpisodeQualityJson",
                table: "PlexSeasonAvailability");

            migrationBuilder.DropColumn(
                name: "AudioCodec",
                table: "PlexMappings");

            migrationBuilder.DropColumn(
                name: "Bitrate",
                table: "PlexMappings");

            migrationBuilder.DropColumn(
                name: "FileSizeBytes",
                table: "PlexMappings");

            migrationBuilder.DropColumn(
                name: "VersionCount",
                table: "PlexMappings");

            migrationBuilder.DropColumn(
                name: "VideoCodec",
                table: "PlexMappings");

            migrationBuilder.DropColumn(
                name: "VideoResolution",
                table: "PlexMappings");
        }
    }
}

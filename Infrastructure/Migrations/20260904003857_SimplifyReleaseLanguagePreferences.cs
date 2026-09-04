using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PlexRequestsHosted.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class SimplifyReleaseLanguagePreferences : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "LanguagePreference",
                table: "QualityProfiles",
                type: "INTEGER",
                nullable: false,
                // Existing administrator-created profiles keep their previous no-preference behavior.
                // The stock-profile startup migration opts Everyday into Smart explicitly.
                defaultValue: 5);

            migrationBuilder.AddColumn<bool>(
                name: "PreferForcedSubtitles",
                table: "QualityProfiles",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "PreferredAudioLanguage",
                table: "QualityProfiles",
                type: "TEXT",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PreferredSubtitleLanguage",
                table: "QualityProfiles",
                type: "TEXT",
                maxLength: 16,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LanguagePreference",
                table: "QualityProfiles");

            migrationBuilder.DropColumn(
                name: "PreferForcedSubtitles",
                table: "QualityProfiles");

            migrationBuilder.DropColumn(
                name: "PreferredAudioLanguage",
                table: "QualityProfiles");

            migrationBuilder.DropColumn(
                name: "PreferredSubtitleLanguage",
                table: "QualityProfiles");
        }
    }
}

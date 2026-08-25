using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PlexRequestsHosted.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddMediaLanguagePolicy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AllowUnknownTrackLanguage",
                table: "QualityProfiles",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "RequireForcedSubtitle",
                table: "QualityProfiles",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "RequiredAudioLanguagesCsv",
                table: "QualityProfiles",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RequiredSubtitleLanguagesCsv",
                table: "QualityProfiles",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "MatchAnime",
                table: "ProfileAssignmentRules",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MediaTracksJson",
                table: "ImportedFiles",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MediaLanguagePolicyJson",
                table: "FulfillmentJobs",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AllowUnknownTrackLanguage",
                table: "QualityProfiles");

            migrationBuilder.DropColumn(
                name: "RequireForcedSubtitle",
                table: "QualityProfiles");

            migrationBuilder.DropColumn(
                name: "RequiredAudioLanguagesCsv",
                table: "QualityProfiles");

            migrationBuilder.DropColumn(
                name: "RequiredSubtitleLanguagesCsv",
                table: "QualityProfiles");

            migrationBuilder.DropColumn(
                name: "MatchAnime",
                table: "ProfileAssignmentRules");

            migrationBuilder.DropColumn(
                name: "MediaTracksJson",
                table: "ImportedFiles");

            migrationBuilder.DropColumn(
                name: "MediaLanguagePolicyJson",
                table: "FulfillmentJobs");
        }
    }
}

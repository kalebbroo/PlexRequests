using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PlexRequestsHosted.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddQualityProfiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AllowedQualityProfileIdsCsv",
                table: "UserProfiles",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DefaultQualityProfileIdMovie",
                table: "UserProfiles",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DefaultQualityProfileIdTv",
                table: "UserProfiles",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "AchievedFormatScore",
                table: "MediaRequests",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "AchievedQualityDefinitionId",
                table: "MediaRequests",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "QualityProfileId",
                table: "MediaRequests",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CustomFormatScore",
                table: "ImportedFiles",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "InfoHash",
                table: "ImportedFiles",
                type: "TEXT",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "QualityDefinitionId",
                table: "ImportedFiles",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReleaseName",
                table: "ImportedFiles",
                type: "TEXT",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "EmptySearchCount",
                table: "FulfillmentJobs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "QualityProfileId",
                table: "FulfillmentJobs",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RelaxFloorAfterEmptySearches",
                table: "DownloadPreferences",
                type: "INTEGER",
                nullable: false,
                // Matches the entity default. EF's 0 would mean "relax the quality floor after zero failed
                // searches" for the existing settings row — i.e. never hold out for the preferred quality at
                // all, which is worse than the behaviour being fixed.
                defaultValue: 3);

            migrationBuilder.CreateTable(
                name: "QualityDefinitions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Resolution = table.Column<int>(type: "INTEGER", nullable: false),
                    Source = table.Column<int>(type: "INTEGER", nullable: false),
                    SortWeight = table.Column<int>(type: "INTEGER", nullable: false),
                    IsSystem = table.Column<bool>(type: "INTEGER", nullable: false),
                    MinSizeGb = table.Column<double>(type: "REAL", nullable: true),
                    MaxSizeGb = table.Column<double>(type: "REAL", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QualityDefinitions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "QualityProfiles",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    IsDefault = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsUserSelectable = table.Column<bool>(type: "INTEGER", nullable: false),
                    AppliesToMediaTypes = table.Column<int>(type: "INTEGER", nullable: false),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    ItemsJson = table.Column<string>(type: "TEXT", nullable: false),
                    CutoffQualityDefinitionId = table.Column<int>(type: "INTEGER", nullable: false),
                    UpgradeAllowed = table.Column<bool>(type: "INTEGER", nullable: false),
                    MinCustomFormatScore = table.Column<int>(type: "INTEGER", nullable: false),
                    CutoffFormatScore = table.Column<int>(type: "INTEGER", nullable: false),
                    MinSizeGb = table.Column<double>(type: "REAL", nullable: true),
                    MaxSizeGb = table.Column<double>(type: "REAL", nullable: true),
                    MaxSeasonPackSizeGb = table.Column<double>(type: "REAL", nullable: true),
                    MinSeeders = table.Column<int>(type: "INTEGER", nullable: true),
                    AllowedLanguagesCsv = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QualityProfiles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ProfileAssignmentRules",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Order = table.Column<int>(type: "INTEGER", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    MatchMediaType = table.Column<int>(type: "INTEGER", nullable: true),
                    MatchGenre = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    MatchTmdbId = table.Column<int>(type: "INTEGER", nullable: true),
                    MatchLibrary = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    QualityProfileId = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProfileAssignmentRules", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProfileAssignmentRules_QualityProfiles_QualityProfileId",
                        column: x => x.QualityProfileId,
                        principalTable: "QualityProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ProfileAssignmentRules_Order",
                table: "ProfileAssignmentRules",
                column: "Order");

            migrationBuilder.CreateIndex(
                name: "IX_ProfileAssignmentRules_QualityProfileId",
                table: "ProfileAssignmentRules",
                column: "QualityProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_QualityDefinitions_Resolution_Source",
                table: "QualityDefinitions",
                columns: new[] { "Resolution", "Source" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_QualityDefinitions_SortWeight",
                table: "QualityDefinitions",
                column: "SortWeight");

            migrationBuilder.CreateIndex(
                name: "IX_QualityProfiles_IsDefault",
                table: "QualityProfiles",
                column: "IsDefault");

            migrationBuilder.CreateIndex(
                name: "IX_QualityProfiles_Name",
                table: "QualityProfiles",
                column: "Name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProfileAssignmentRules");

            migrationBuilder.DropTable(
                name: "QualityDefinitions");

            migrationBuilder.DropTable(
                name: "QualityProfiles");

            migrationBuilder.DropColumn(
                name: "AllowedQualityProfileIdsCsv",
                table: "UserProfiles");

            migrationBuilder.DropColumn(
                name: "DefaultQualityProfileIdMovie",
                table: "UserProfiles");

            migrationBuilder.DropColumn(
                name: "DefaultQualityProfileIdTv",
                table: "UserProfiles");

            migrationBuilder.DropColumn(
                name: "AchievedFormatScore",
                table: "MediaRequests");

            migrationBuilder.DropColumn(
                name: "AchievedQualityDefinitionId",
                table: "MediaRequests");

            migrationBuilder.DropColumn(
                name: "QualityProfileId",
                table: "MediaRequests");

            migrationBuilder.DropColumn(
                name: "CustomFormatScore",
                table: "ImportedFiles");

            migrationBuilder.DropColumn(
                name: "InfoHash",
                table: "ImportedFiles");

            migrationBuilder.DropColumn(
                name: "QualityDefinitionId",
                table: "ImportedFiles");

            migrationBuilder.DropColumn(
                name: "ReleaseName",
                table: "ImportedFiles");

            migrationBuilder.DropColumn(
                name: "EmptySearchCount",
                table: "FulfillmentJobs");

            migrationBuilder.DropColumn(
                name: "QualityProfileId",
                table: "FulfillmentJobs");

            migrationBuilder.DropColumn(
                name: "RelaxFloorAfterEmptySearches",
                table: "DownloadPreferences");
        }
    }
}

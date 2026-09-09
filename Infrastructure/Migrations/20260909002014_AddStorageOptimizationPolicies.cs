using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PlexRequestsHosted.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddStorageOptimizationPolicies : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BlockedCustomFormatIdsCsv",
                table: "QualityProfiles",
                type: "TEXT",
                maxLength: 2048,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RequiredCustomFormatIdsCsv",
                table: "QualityProfiles",
                type: "TEXT",
                maxLength: 2048,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StorageOptimizationPolicyJson",
                table: "FulfillmentJobs",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BlockedCustomFormatIdsCsv",
                table: "QualityProfiles");

            migrationBuilder.DropColumn(
                name: "RequiredCustomFormatIdsCsv",
                table: "QualityProfiles");

            migrationBuilder.DropColumn(
                name: "StorageOptimizationPolicyJson",
                table: "FulfillmentJobs");
        }
    }
}

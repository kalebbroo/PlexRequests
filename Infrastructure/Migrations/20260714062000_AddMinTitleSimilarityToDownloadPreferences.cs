using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PlexRequestsHosted.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddMinTitleSimilarityToDownloadPreferences : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "MinTitleSimilarity",
                table: "DownloadPreferences",
                type: "REAL",
                nullable: false,
                defaultValue: 0.0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MinTitleSimilarity",
                table: "DownloadPreferences");
        }
    }
}

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PlexRequestsHosted.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDirectYouTubeMusicAcquisition : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Protocol",
                table: "ReleaseBlocklist",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "SourceId",
                table: "ReleaseBlocklist",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "DirectDownloadsEnabled",
                table: "MusicSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.Sql("UPDATE ReleaseBlocklist SET SourceId = lower(InfoHash) WHERE InfoHash IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ReleaseBlocklist_Protocol_SourceId",
                table: "ReleaseBlocklist",
                columns: new[] { "Protocol", "SourceId" });

            migrationBuilder.CreateIndex(
                name: "IX_ReleaseBlocklist_Protocol_SourceId_MediaRequestId",
                table: "ReleaseBlocklist",
                columns: new[] { "Protocol", "SourceId", "MediaRequestId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ReleaseBlocklist_Protocol_SourceId",
                table: "ReleaseBlocklist");

            migrationBuilder.DropIndex(
                name: "IX_ReleaseBlocklist_Protocol_SourceId_MediaRequestId",
                table: "ReleaseBlocklist");

            migrationBuilder.DropColumn(
                name: "Protocol",
                table: "ReleaseBlocklist");

            migrationBuilder.DropColumn(
                name: "SourceId",
                table: "ReleaseBlocklist");

            migrationBuilder.DropColumn(
                name: "DirectDownloadsEnabled",
                table: "MusicSettings");
        }
    }
}

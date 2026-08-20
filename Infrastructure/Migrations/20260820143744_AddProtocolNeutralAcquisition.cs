using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PlexRequestsHosted.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddProtocolNeutralAcquisition : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_FulfillmentTorrents_FulfillmentJobId_TorrentId",
                table: "FulfillmentTorrents");

            migrationBuilder.AddColumn<int>(
                name: "Protocol",
                table: "ImportedFiles",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Protocol",
                table: "FulfillmentTorrents",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_FulfillmentTorrents_FulfillmentJobId_Protocol_TorrentId",
                table: "FulfillmentTorrents",
                columns: new[] { "FulfillmentJobId", "Protocol", "TorrentId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_FulfillmentTorrents_FulfillmentJobId_Protocol_TorrentId",
                table: "FulfillmentTorrents");

            migrationBuilder.DropColumn(
                name: "Protocol",
                table: "ImportedFiles");

            migrationBuilder.DropColumn(
                name: "Protocol",
                table: "FulfillmentTorrents");

            migrationBuilder.CreateIndex(
                name: "IX_FulfillmentTorrents_FulfillmentJobId_TorrentId",
                table: "FulfillmentTorrents",
                columns: new[] { "FulfillmentJobId", "TorrentId" },
                unique: true);
        }
    }
}

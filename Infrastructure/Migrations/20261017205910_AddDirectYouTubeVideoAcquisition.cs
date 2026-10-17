using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PlexRequestsHosted.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDirectYouTubeVideoAcquisition : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DirectVideoContextJson",
                table: "FulfillmentJobs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "DirectVideoSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    IsSingleton = table.Column<bool>(type: "INTEGER", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    ChannelAllowlist = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DirectVideoSettings", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DirectVideoSettings");

            migrationBuilder.DropColumn(
                name: "DirectVideoContextJson",
                table: "FulfillmentJobs");
        }
    }
}

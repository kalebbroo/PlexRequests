using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PlexRequestsHosted.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddNetworkShares : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "NetworkShares",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    MountSlug = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Protocol = table.Column<int>(type: "INTEGER", nullable: false),
                    Server = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    ShareName = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    Domain = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    Username = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    PasswordProtected = table.Column<string>(type: "TEXT", nullable: true),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NetworkShares", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_NetworkShares_MountSlug",
                table: "NetworkShares",
                column: "MountSlug",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "NetworkShares");
        }
    }
}

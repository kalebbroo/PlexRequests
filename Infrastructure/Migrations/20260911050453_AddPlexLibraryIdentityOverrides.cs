using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PlexRequestsHosted.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPlexLibraryIdentityOverrides : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PlexLibraryIdentityOverrides",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    InventoryKey = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    ExternalKey = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    MediaType = table.Column<int>(type: "INTEGER", nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    Year = table.Column<int>(type: "INTEGER", nullable: true),
                    ConfirmedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlexLibraryIdentityOverrides", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PlexLibraryIdentityOverrides_ExternalKey",
                table: "PlexLibraryIdentityOverrides",
                column: "ExternalKey");

            migrationBuilder.CreateIndex(
                name: "IX_PlexLibraryIdentityOverrides_InventoryKey",
                table: "PlexLibraryIdentityOverrides",
                column: "InventoryKey",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PlexLibraryIdentityOverrides");
        }
    }
}

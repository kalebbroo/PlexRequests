using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PlexRequestsHosted.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddQualityRules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "QualityRules",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Order = table.Column<int>(type: "INTEGER", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsDefault = table.Column<bool>(type: "INTEGER", nullable: false),
                    MatchMediaType = table.Column<int>(type: "INTEGER", nullable: true),
                    MatchGenre = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    MatchTmdbId = table.Column<int>(type: "INTEGER", nullable: true),
                    MatchLibrary = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    TargetQuality = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QualityRules", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_QualityRules_Order",
                table: "QualityRules",
                column: "Order");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "QualityRules");
        }
    }
}

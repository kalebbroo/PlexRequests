using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PlexRequestsHosted.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddRecommendedFeed : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "EnableRecommendedFeed",
                table: "Indexers",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "RecommendedItems",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SourceTitle = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    SourceYear = table.Column<int>(type: "INTEGER", nullable: true),
                    MediaType = table.Column<int>(type: "INTEGER", nullable: false),
                    TmdbId = table.Column<int>(type: "INTEGER", nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    PosterUrl = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    Year = table.Column<int>(type: "INTEGER", nullable: true),
                    SourceIndexerId = table.Column<int>(type: "INTEGER", nullable: false),
                    Rank = table.Column<int>(type: "INTEGER", nullable: false),
                    SeenAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecommendedItems", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RecommendedItems_Rank",
                table: "RecommendedItems",
                column: "Rank");

            migrationBuilder.CreateIndex(
                name: "IX_RecommendedItems_SeenAt",
                table: "RecommendedItems",
                column: "SeenAt");

            migrationBuilder.CreateIndex(
                name: "IX_RecommendedItems_TmdbId_MediaType",
                table: "RecommendedItems",
                columns: new[] { "TmdbId", "MediaType" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RecommendedItems");

            migrationBuilder.DropColumn(
                name: "EnableRecommendedFeed",
                table: "Indexers");
        }
    }
}

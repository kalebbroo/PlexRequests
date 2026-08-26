using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PlexRequestsHosted.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddImportedEpisodeCoverage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ImportedEpisodeCoverage",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ImportedFileId = table.Column<int>(type: "INTEGER", nullable: false),
                    SeasonNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    EpisodeNumber = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ImportedEpisodeCoverage", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ImportedEpisodeCoverage_ImportedFiles_ImportedFileId",
                        column: x => x.ImportedFileId,
                        principalTable: "ImportedFiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            // Preserve the one-episode identity of every existing audit row. New workers can write several
            // children per physical file, while old imports remain fully queryable through the same table.
            migrationBuilder.Sql("""
                INSERT INTO ImportedEpisodeCoverage (ImportedFileId, SeasonNumber, EpisodeNumber)
                SELECT Id, SeasonNumber, EpisodeNumber
                FROM ImportedFiles
                WHERE SeasonNumber IS NOT NULL AND EpisodeNumber IS NOT NULL;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_ImportedEpisodeCoverage_ImportedFileId_SeasonNumber_EpisodeNumber",
                table: "ImportedEpisodeCoverage",
                columns: new[] { "ImportedFileId", "SeasonNumber", "EpisodeNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ImportedEpisodeCoverage_SeasonNumber_EpisodeNumber",
                table: "ImportedEpisodeCoverage",
                columns: new[] { "SeasonNumber", "EpisodeNumber" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ImportedEpisodeCoverage");
        }
    }
}

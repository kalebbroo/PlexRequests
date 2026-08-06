using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PlexRequestsHosted.Infrastructure.Migrations
{
    /// <summary>
    /// Turns the settings-only IndexerSettings table into a full Indexers table that can describe an actual
    /// endpoint (URL, key, categories, limits), so Torznab/Newznab aggregators become first-class rows
    /// instead of collapsing onto one shared "Torznab" entry.
    ///
    /// Hand-written as a RENAME plus column additions. EF scaffolded a drop-and-recreate, which would have
    /// thrown away every row's accumulated health (search/result totals, failure streaks, last error) and
    /// any priority or enable state the admin had set.
    /// </summary>
    public partial class AddIndexerEndpoints : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(name: "IX_IndexerSettings_Name", table: "IndexerSettings");
            migrationBuilder.RenameTable(name: "IndexerSettings", newName: "Indexers");

            migrationBuilder.AddColumn<string>(name: "Implementation", table: "Indexers", type: "TEXT", maxLength: 32, nullable: false, defaultValue: "");
            migrationBuilder.AddColumn<bool>(name: "IsBuiltIn", table: "Indexers", type: "INTEGER", nullable: false, defaultValue: false);
            migrationBuilder.AddColumn<string>(name: "Url", table: "Indexers", type: "TEXT", maxLength: 2048, nullable: true);
            migrationBuilder.AddColumn<string>(name: "ApiKeyEncrypted", table: "Indexers", type: "TEXT", maxLength: 1024, nullable: true);
            migrationBuilder.AddColumn<string>(name: "BaseUrlOverride", table: "Indexers", type: "TEXT", maxLength: 2048, nullable: true);

            migrationBuilder.AddColumn<string>(name: "MovieCategoriesCsv", table: "Indexers", type: "TEXT", maxLength: 256, nullable: true);
            migrationBuilder.AddColumn<string>(name: "TvCategoriesCsv", table: "Indexers", type: "TEXT", maxLength: 256, nullable: true);
            migrationBuilder.AddColumn<string>(name: "AnimeCategoriesCsv", table: "Indexers", type: "TEXT", maxLength: 256, nullable: true);

            // Default true; the backfill below narrows each built-in to what it can actually serve.
            migrationBuilder.AddColumn<bool>(name: "SupportsMovie", table: "Indexers", type: "INTEGER", nullable: false, defaultValue: true);
            migrationBuilder.AddColumn<bool>(name: "SupportsTv", table: "Indexers", type: "INTEGER", nullable: false, defaultValue: true);
            migrationBuilder.AddColumn<bool>(name: "SupportsAnime", table: "Indexers", type: "INTEGER", nullable: false, defaultValue: true);
            migrationBuilder.AddColumn<bool>(name: "AnimeOnly", table: "Indexers", type: "INTEGER", nullable: false, defaultValue: false);

            migrationBuilder.AddColumn<bool>(name: "EnableRss", table: "Indexers", type: "INTEGER", nullable: false, defaultValue: true);
            migrationBuilder.AddColumn<bool>(name: "EnableAutomaticSearch", table: "Indexers", type: "INTEGER", nullable: false, defaultValue: true);
            migrationBuilder.AddColumn<bool>(name: "EnableInteractiveSearch", table: "Indexers", type: "INTEGER", nullable: false, defaultValue: true);

            migrationBuilder.AddColumn<int>(name: "MinSeeders", table: "Indexers", type: "INTEGER", nullable: true);
            migrationBuilder.AddColumn<double>(name: "SeedRatio", table: "Indexers", type: "REAL", nullable: true);
            migrationBuilder.AddColumn<int>(name: "SeedTimeMinutes", table: "Indexers", type: "INTEGER", nullable: true);
            migrationBuilder.AddColumn<int>(name: "SeasonPackSeedTimeMinutes", table: "Indexers", type: "INTEGER", nullable: true);
            migrationBuilder.AddColumn<int>(name: "TimeoutSeconds", table: "Indexers", type: "INTEGER", nullable: true);

            // Non-zero defaults matter here: EF's 0 would mean no requests allowed, no caching and no detail
            // fetches at all for every pre-existing row.
            migrationBuilder.AddColumn<int>(name: "RateLimitPerMinute", table: "Indexers", type: "INTEGER", nullable: false, defaultValue: 30);
            migrationBuilder.AddColumn<int>(name: "CacheSeconds", table: "Indexers", type: "INTEGER", nullable: false, defaultValue: 300);
            migrationBuilder.AddColumn<int>(name: "MaxDetailFetches", table: "Indexers", type: "INTEGER", nullable: false, defaultValue: 25);
            migrationBuilder.AddColumn<int>(name: "MaxAgeDays", table: "Indexers", type: "INTEGER", nullable: true);
            migrationBuilder.AddColumn<string>(name: "Notes", table: "Indexers", type: "TEXT", maxLength: 512, nullable: true);

            migrationBuilder.CreateIndex(name: "IX_Indexers_Name", table: "Indexers", column: "Name", unique: true);
            migrationBuilder.CreateIndex(name: "IX_Indexers_Enabled_Priority", table: "Indexers", columns: new[] { "Enabled", "Priority" });
            migrationBuilder.CreateIndex(name: "IX_Indexers_Implementation", table: "Indexers", column: "Implementation");

            // Existing rows were keyed by provider name, which is exactly the implementation key.
            migrationBuilder.Sql("UPDATE Indexers SET Implementation = Name, IsBuiltIn = 1 WHERE Implementation = '';");

            // Restore the per-provider capabilities that used to be compiled into each provider class.
            migrationBuilder.Sql("UPDATE Indexers SET SupportsMovie = 0, SupportsTv = 1, SupportsAnime = 1 WHERE Name = 'EZTV';");
            migrationBuilder.Sql("UPDATE Indexers SET SupportsMovie = 1, SupportsTv = 0, SupportsAnime = 0 WHERE Name = 'YTS';");
            migrationBuilder.Sql("UPDATE Indexers SET AnimeOnly = 1 WHERE Name = 'Nyaa';");

            // The old "Torznab" row was a rollup of every configured endpoint behind one toggle. It can't
            // search anything on its own now that endpoints are individual rows, so retire it rather than
            // leave a dead entry that looks configurable. Its history is preserved under the new name.
            migrationBuilder.Sql(@"
                UPDATE Indexers
                SET Name = 'Torznab (legacy rollup)', Enabled = 0, IsBuiltIn = 0
                WHERE Name = 'Torznab';");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var col in new[]
            {
                "Implementation", "IsBuiltIn", "Url", "ApiKeyEncrypted", "BaseUrlOverride",
                "MovieCategoriesCsv", "TvCategoriesCsv", "AnimeCategoriesCsv",
                "SupportsMovie", "SupportsTv", "SupportsAnime", "AnimeOnly",
                "EnableRss", "EnableAutomaticSearch", "EnableInteractiveSearch",
                "MinSeeders", "SeedRatio", "SeedTimeMinutes", "SeasonPackSeedTimeMinutes",
                "TimeoutSeconds", "RateLimitPerMinute", "CacheSeconds", "MaxDetailFetches",
                "MaxAgeDays", "Notes"
            })
            {
                migrationBuilder.DropColumn(name: col, table: "Indexers");
            }

            migrationBuilder.DropIndex(name: "IX_Indexers_Name", table: "Indexers");
            migrationBuilder.DropIndex(name: "IX_Indexers_Enabled_Priority", table: "Indexers");
            migrationBuilder.DropIndex(name: "IX_Indexers_Implementation", table: "Indexers");
            migrationBuilder.RenameTable(name: "Indexers", newName: "IndexerSettings");
            migrationBuilder.CreateIndex(name: "IX_IndexerSettings_Name", table: "IndexerSettings", column: "Name", unique: true);
        }
    }
}

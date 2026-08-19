using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PlexRequestsHosted.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddModularMediaFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "MediaIdentityId",
                table: "Watchlist",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MediaIdentityId",
                table: "MediaRequests",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RequestScopeKind",
                table: "MediaRequests",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "MediaIdentityId",
                table: "FulfillmentJobs",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "IndexerMediaCapabilities",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    IndexerId = table.Column<int>(type: "INTEGER", nullable: false),
                    MediaType = table.Column<int>(type: "INTEGER", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    CategoriesCsv = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IndexerMediaCapabilities", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IndexerMediaCapabilities_Indexers_IndexerId",
                        column: x => x.IndexerId,
                        principalTable: "Indexers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MediaIdentities",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    MediaType = table.Column<int>(type: "INTEGER", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    PrimaryProvider = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    PrimaryId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    StableKey = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MediaIdentities", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MediaExternalIdentifiers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    MediaIdentityId = table.Column<int>(type: "INTEGER", nullable: false),
                    MediaType = table.Column<int>(type: "INTEGER", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    Provider = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    ExternalId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    IsPrimary = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MediaExternalIdentifiers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MediaExternalIdentifiers_MediaIdentities_MediaIdentityId",
                        column: x => x.MediaIdentityId,
                        principalTable: "MediaIdentities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            // Create this before backfilling so INSERT OR IGNORE is genuinely idempotent even if legacy
            // provider ids differ only by case but normalize to the same stable key.
            migrationBuilder.CreateIndex(
                name: "IX_MediaIdentities_StableKey",
                table: "MediaIdentities",
                column: "StableKey",
                unique: true);

            // Preserve the intent of every existing request. The new enum is not inferred at runtime from
            // TV-only CSV columns, so backfill it once while those columns are still authoritative.
            migrationBuilder.Sql("""
                UPDATE MediaRequests
                SET RequestScopeKind = CASE
                    WHEN MediaType = 0 THEN 0
                    WHEN MediaType IN (1, 3) AND RequestedEpisodesCsv IS NOT NULL AND trim(RequestedEpisodesCsv) <> '' THEN 3
                    WHEN MediaType IN (1, 3) AND RequestedSeasonsCsv IS NOT NULL AND trim(RequestedSeasonsCsv) <> '' THEN 2
                    WHEN MediaType IN (1, 3) THEN 1
                    WHEN MediaType = 2 THEN 4
                    ELSE 0
                END;
                """);

            // Canonical identities for request rows. StableKey mirrors MediaRef.StableKey exactly. INSERT
            // OR IGNORE makes repeated legacy ids converge on one identity instead of duplicating it.
            migrationBuilder.Sql("""
                INSERT OR IGNORE INTO MediaIdentities
                    (MediaType, Kind, PrimaryProvider, PrimaryId, StableKey, CreatedAt, UpdatedAt)
                SELECT DISTINCT
                    MediaType,
                    CASE WHEN MediaType = 0 THEN 0 WHEN MediaType IN (1, 3) THEN 1 ELSE 2 END,
                    CASE
                        WHEN ExternalSource IS NOT NULL AND trim(ExternalSource) <> '' THEN lower(trim(ExternalSource))
                        WHEN ExternalId IS NOT NULL AND trim(ExternalId) <> '' THEN 'external'
                        ELSE 'tmdb'
                    END,
                    CASE
                        WHEN ExternalId IS NOT NULL AND trim(ExternalId) <> '' THEN
                            CASE WHEN lower(trim(coalesce(ExternalSource, ''))) IN ('musicbrainz', 'tmdb')
                                THEN lower(trim(ExternalId)) ELSE trim(ExternalId) END
                        ELSE CAST(MediaId AS TEXT)
                    END,
                    (CASE MediaType WHEN 0 THEN 'Movie' WHEN 1 THEN 'TvShow' WHEN 2 THEN 'Music' ELSE 'Anime' END)
                        || ':' ||
                    (CASE WHEN MediaType = 0 THEN 'Movie' WHEN MediaType IN (1, 3) THEN 'Series' ELSE 'Album' END)
                        || ':' ||
                    CASE
                        WHEN ExternalSource IS NOT NULL AND trim(ExternalSource) <> '' THEN lower(trim(ExternalSource))
                        WHEN ExternalId IS NOT NULL AND trim(ExternalId) <> '' THEN 'external'
                        ELSE 'tmdb'
                    END
                        || ':' || CASE
                            WHEN ExternalId IS NOT NULL AND trim(ExternalId) <> '' THEN
                                CASE WHEN lower(trim(coalesce(ExternalSource, ''))) IN ('musicbrainz', 'tmdb')
                                    THEN lower(trim(ExternalId)) ELSE trim(ExternalId) END
                            ELSE CAST(MediaId AS TEXT)
                        END,
                    CURRENT_TIMESTAMP,
                    CURRENT_TIMESTAMP
                FROM MediaRequests;
                """);

            // Watchlist rows predate provider ids and are TMDb-backed. Add only identities not already
            // introduced by requests, then point both tables at the shared row.
            migrationBuilder.Sql("""
                INSERT OR IGNORE INTO MediaIdentities
                    (MediaType, Kind, PrimaryProvider, PrimaryId, StableKey, CreatedAt, UpdatedAt)
                SELECT DISTINCT
                    MediaType,
                    CASE WHEN MediaType = 0 THEN 0 WHEN MediaType IN (1, 3) THEN 1 ELSE 2 END,
                    'tmdb', CAST(MediaId AS TEXT),
                    (CASE MediaType WHEN 0 THEN 'Movie' WHEN 1 THEN 'TvShow' WHEN 2 THEN 'Music' ELSE 'Anime' END)
                        || ':' ||
                    (CASE WHEN MediaType = 0 THEN 'Movie' WHEN MediaType IN (1, 3) THEN 'Series' ELSE 'Album' END)
                        || ':tmdb:' || lower(CAST(MediaId AS TEXT)),
                    CURRENT_TIMESTAMP,
                    CURRENT_TIMESTAMP
                FROM Watchlist w
                WHERE NOT EXISTS (
                    SELECT 1 FROM MediaIdentities mi
                    WHERE mi.StableKey =
                        (CASE w.MediaType WHEN 0 THEN 'Movie' WHEN 1 THEN 'TvShow' WHEN 2 THEN 'Music' ELSE 'Anime' END)
                        || ':' ||
                        (CASE WHEN w.MediaType = 0 THEN 'Movie' WHEN w.MediaType IN (1, 3) THEN 'Series' ELSE 'Album' END)
                        || ':tmdb:' || lower(CAST(w.MediaId AS TEXT))
                );
                """);

            migrationBuilder.Sql("""
                INSERT OR IGNORE INTO MediaExternalIdentifiers
                    (MediaIdentityId, MediaType, Kind, Provider, ExternalId, IsPrimary)
                SELECT Id, MediaType, Kind, PrimaryProvider, PrimaryId, 1 FROM MediaIdentities;

                UPDATE MediaRequests
                SET MediaIdentityId = (
                    SELECT mi.Id FROM MediaIdentities mi
                    WHERE mi.StableKey =
                        (CASE MediaRequests.MediaType WHEN 0 THEN 'Movie' WHEN 1 THEN 'TvShow' WHEN 2 THEN 'Music' ELSE 'Anime' END)
                        || ':' ||
                        (CASE WHEN MediaRequests.MediaType = 0 THEN 'Movie' WHEN MediaRequests.MediaType IN (1, 3) THEN 'Series' ELSE 'Album' END)
                        || ':' ||
                        CASE
                            WHEN MediaRequests.ExternalSource IS NOT NULL AND trim(MediaRequests.ExternalSource) <> '' THEN lower(trim(MediaRequests.ExternalSource))
                            WHEN MediaRequests.ExternalId IS NOT NULL AND trim(MediaRequests.ExternalId) <> '' THEN 'external'
                            ELSE 'tmdb'
                        END
                        || ':' || CASE
                            WHEN MediaRequests.ExternalId IS NOT NULL AND trim(MediaRequests.ExternalId) <> '' THEN
                                CASE WHEN lower(trim(coalesce(MediaRequests.ExternalSource, ''))) IN ('musicbrainz', 'tmdb')
                                    THEN lower(trim(MediaRequests.ExternalId)) ELSE trim(MediaRequests.ExternalId) END
                            ELSE CAST(MediaRequests.MediaId AS TEXT)
                        END
                );

                UPDATE Watchlist
                SET MediaIdentityId = (
                    SELECT mi.Id FROM MediaIdentities mi
                    WHERE mi.StableKey =
                        (CASE Watchlist.MediaType WHEN 0 THEN 'Movie' WHEN 1 THEN 'TvShow' WHEN 2 THEN 'Music' ELSE 'Anime' END)
                        || ':' ||
                        (CASE WHEN Watchlist.MediaType = 0 THEN 'Movie' WHEN Watchlist.MediaType IN (1, 3) THEN 'Series' ELSE 'Album' END)
                        || ':tmdb:' || lower(CAST(Watchlist.MediaId AS TEXT))
                );

                UPDATE FulfillmentJobs
                SET MediaIdentityId = (
                    SELECT mr.MediaIdentityId FROM MediaRequests mr
                    WHERE mr.Id = FulfillmentJobs.MediaRequestId
                );
                """);

            // Turn the old fixed columns into capability rows. Music is deliberately disabled until its
            // end-to-end fulfillment module lands; an upgrade must never silently broaden an indexer.
            migrationBuilder.Sql("""
                INSERT OR IGNORE INTO IndexerMediaCapabilities (IndexerId, MediaType, Enabled, CategoriesCsv)
                    SELECT Id, 0, SupportsMovie, MovieCategoriesCsv FROM Indexers;
                INSERT OR IGNORE INTO IndexerMediaCapabilities (IndexerId, MediaType, Enabled, CategoriesCsv)
                    SELECT Id, 1, SupportsTv, TvCategoriesCsv FROM Indexers;
                INSERT OR IGNORE INTO IndexerMediaCapabilities (IndexerId, MediaType, Enabled, CategoriesCsv)
                    SELECT Id, 3, SupportsAnime, AnimeCategoriesCsv FROM Indexers;
                INSERT OR IGNORE INTO IndexerMediaCapabilities (IndexerId, MediaType, Enabled, CategoriesCsv)
                    SELECT Id, 2, 0, NULL FROM Indexers;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_Watchlist_MediaIdentityId",
                table: "Watchlist",
                column: "MediaIdentityId");

            migrationBuilder.CreateIndex(
                name: "IX_MediaRequests_MediaIdentityId",
                table: "MediaRequests",
                column: "MediaIdentityId");

            migrationBuilder.CreateIndex(
                name: "IX_FulfillmentJobs_MediaIdentityId",
                table: "FulfillmentJobs",
                column: "MediaIdentityId");

            migrationBuilder.CreateIndex(
                name: "IX_IndexerMediaCapabilities_IndexerId_MediaType",
                table: "IndexerMediaCapabilities",
                columns: new[] { "IndexerId", "MediaType" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MediaExternalIdentifiers_MediaType_Kind_Provider_ExternalId",
                table: "MediaExternalIdentifiers",
                columns: new[] { "MediaType", "Kind", "Provider", "ExternalId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MediaExternalIdentifiers_MediaIdentityId",
                table: "MediaExternalIdentifiers",
                column: "MediaIdentityId");

            migrationBuilder.CreateIndex(
                name: "IX_MediaIdentities_MediaType_Kind",
                table: "MediaIdentities",
                columns: new[] { "MediaType", "Kind" });

            migrationBuilder.AddForeignKey(
                name: "FK_FulfillmentJobs_MediaIdentities_MediaIdentityId",
                table: "FulfillmentJobs",
                column: "MediaIdentityId",
                principalTable: "MediaIdentities",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_MediaRequests_MediaIdentities_MediaIdentityId",
                table: "MediaRequests",
                column: "MediaIdentityId",
                principalTable: "MediaIdentities",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Watchlist_MediaIdentities_MediaIdentityId",
                table: "Watchlist",
                column: "MediaIdentityId",
                principalTable: "MediaIdentities",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_FulfillmentJobs_MediaIdentities_MediaIdentityId",
                table: "FulfillmentJobs");

            migrationBuilder.DropForeignKey(
                name: "FK_MediaRequests_MediaIdentities_MediaIdentityId",
                table: "MediaRequests");

            migrationBuilder.DropForeignKey(
                name: "FK_Watchlist_MediaIdentities_MediaIdentityId",
                table: "Watchlist");

            migrationBuilder.DropTable(
                name: "IndexerMediaCapabilities");

            migrationBuilder.DropTable(
                name: "MediaExternalIdentifiers");

            migrationBuilder.DropTable(
                name: "MediaIdentities");

            migrationBuilder.DropIndex(
                name: "IX_Watchlist_MediaIdentityId",
                table: "Watchlist");

            migrationBuilder.DropIndex(
                name: "IX_MediaRequests_MediaIdentityId",
                table: "MediaRequests");

            migrationBuilder.DropIndex(
                name: "IX_FulfillmentJobs_MediaIdentityId",
                table: "FulfillmentJobs");

            migrationBuilder.DropColumn(
                name: "MediaIdentityId",
                table: "Watchlist");

            migrationBuilder.DropColumn(
                name: "MediaIdentityId",
                table: "MediaRequests");

            migrationBuilder.DropColumn(
                name: "RequestScopeKind",
                table: "MediaRequests");

            migrationBuilder.DropColumn(
                name: "MediaIdentityId",
                table: "FulfillmentJobs");
        }
    }
}

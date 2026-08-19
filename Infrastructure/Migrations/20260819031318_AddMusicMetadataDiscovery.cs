using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PlexRequestsHosted.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddMusicMetadataDiscovery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MediaMetadataCache_MediaType_TmdbId",
                table: "MediaMetadataCache");

            migrationBuilder.AddColumn<int>(
                name: "MediaIdentityId",
                table: "MediaMetadataCache",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "MusicSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    IsSingleton = table.Column<bool>(type: "INTEGER", nullable: false),
                    CatalogEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MusicSettings", x => x.Id);
                });

            // Existing metadata rows are TMDb-backed. Attach them to the same canonical identities used
            // by requests/jobs so cache ownership remains provider-neutral after this migration.
            migrationBuilder.Sql("""
                INSERT OR IGNORE INTO MediaIdentities
                    (MediaType, Kind, PrimaryProvider, PrimaryId, StableKey, CreatedAt, UpdatedAt)
                SELECT DISTINCT
                    MediaType,
                    CASE WHEN MediaType = 0 THEN 0 ELSE 1 END,
                    'tmdb', CAST(TmdbId AS TEXT),
                    (CASE MediaType WHEN 0 THEN 'Movie' WHEN 1 THEN 'TvShow' ELSE 'Anime' END)
                        || ':' || (CASE WHEN MediaType = 0 THEN 'Movie' ELSE 'Series' END)
                        || ':tmdb:' || CAST(TmdbId AS TEXT),
                    CURRENT_TIMESTAMP, CURRENT_TIMESTAMP
                FROM MediaMetadataCache
                WHERE TmdbId > 0 AND MediaType IN (0, 1, 3);

                INSERT OR IGNORE INTO MediaExternalIdentifiers
                    (MediaIdentityId, MediaType, Kind, Provider, ExternalId, IsPrimary)
                SELECT mi.Id, mi.MediaType, mi.Kind, 'tmdb', mi.PrimaryId, 1
                FROM MediaIdentities mi
                WHERE mi.PrimaryProvider = 'tmdb';

                UPDATE MediaMetadataCache
                SET MediaIdentityId = (
                    SELECT mi.Id FROM MediaIdentities mi
                    WHERE mi.StableKey =
                        (CASE MediaMetadataCache.MediaType WHEN 0 THEN 'Movie' WHEN 1 THEN 'TvShow' ELSE 'Anime' END)
                        || ':' || (CASE WHEN MediaMetadataCache.MediaType = 0 THEN 'Movie' ELSE 'Series' END)
                        || ':tmdb:' || CAST(MediaMetadataCache.TmdbId AS TEXT)
                )
                WHERE TmdbId > 0 AND MediaType IN (0, 1, 3);

                INSERT INTO MusicSettings (IsSingleton, CatalogEnabled, UpdatedAt)
                VALUES (1, 0, CURRENT_TIMESTAMP);
                """);

            migrationBuilder.CreateIndex(
                name: "IX_MediaMetadataCache_MediaIdentityId",
                table: "MediaMetadataCache",
                column: "MediaIdentityId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MediaMetadataCache_MediaType_TmdbId",
                table: "MediaMetadataCache",
                columns: new[] { "MediaType", "TmdbId" },
                unique: true,
                filter: "\"TmdbId\" > 0");

            migrationBuilder.CreateIndex(
                name: "IX_MusicSettings_IsSingleton",
                table: "MusicSettings",
                column: "IsSingleton",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_MediaMetadataCache_MediaIdentities_MediaIdentityId",
                table: "MediaMetadataCache",
                column: "MediaIdentityId",
                principalTable: "MediaIdentities",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_MediaMetadataCache_MediaIdentities_MediaIdentityId",
                table: "MediaMetadataCache");

            migrationBuilder.DropTable(
                name: "MusicSettings");

            migrationBuilder.DropIndex(
                name: "IX_MediaMetadataCache_MediaIdentityId",
                table: "MediaMetadataCache");

            migrationBuilder.DropIndex(
                name: "IX_MediaMetadataCache_MediaType_TmdbId",
                table: "MediaMetadataCache");

            migrationBuilder.DropColumn(
                name: "MediaIdentityId",
                table: "MediaMetadataCache");

            // The former schema allowed only one (Music, 0) cache row. Provider-neutral music rows cannot
            // be represented after downgrade, so remove derived cache data before restoring that index.
            migrationBuilder.Sql("DELETE FROM MediaMetadataCache WHERE TmdbId <= 0;");

            migrationBuilder.CreateIndex(
                name: "IX_MediaMetadataCache_MediaType_TmdbId",
                table: "MediaMetadataCache",
                columns: new[] { "MediaType", "TmdbId" },
                unique: true);
        }
    }
}

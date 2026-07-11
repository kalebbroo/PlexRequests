using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PlexRequestsHosted.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PlexMappings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ExternalKey = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    RatingKey = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    MediaType = table.Column<int>(type: "INTEGER", nullable: true),
                    Title = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    Year = table.Column<int>(type: "INTEGER", nullable: true),
                    LastSeenAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlexMappings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Users",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Username = table.Column<string>(type: "TEXT", nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", nullable: true),
                    Email = table.Column<string>(type: "TEXT", nullable: true),
                    AvatarUrl = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Users", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MediaRequests",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    MediaId = table.Column<int>(type: "INTEGER", nullable: false),
                    MediaType = table.Column<int>(type: "INTEGER", nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    PosterUrl = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    RequestedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    RequestedByUserId = table.Column<int>(type: "INTEGER", nullable: true),
                    RequestedBy = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    ApprovedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    AvailableAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    DenialReason = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    RequestNote = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    RequestAllSeasons = table.Column<bool>(type: "INTEGER", nullable: false),
                    RequestedSeasonsCsv = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MediaRequests", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MediaRequests_Users_RequestedByUserId",
                        column: x => x.RequestedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "UserProfiles",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<int>(type: "INTEGER", nullable: false),
                    PlexId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    PlexUsername = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    Roles = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastLoginAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ThemeDarkMode = table.Column<bool>(type: "INTEGER", nullable: false),
                    Language = table.Column<string>(type: "TEXT", maxLength: 8, nullable: false),
                    Region = table.Column<string>(type: "TEXT", maxLength: 8, nullable: false),
                    ShowAdultContent = table.Column<bool>(type: "INTEGER", nullable: false),
                    DefaultSort = table.Column<int>(type: "INTEGER", nullable: false),
                    DefaultQualityMovie = table.Column<int>(type: "INTEGER", nullable: false),
                    DefaultQualityTV = table.Column<int>(type: "INTEGER", nullable: false),
                    AutoplayTrailers = table.Column<bool>(type: "INTEGER", nullable: false),
                    WatchedBadges = table.Column<bool>(type: "INTEGER", nullable: false),
                    PreferredProvider = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    MovieRequestLimit = table.Column<int>(type: "INTEGER", nullable: true),
                    TvRequestLimit = table.Column<int>(type: "INTEGER", nullable: true),
                    MusicRequestLimit = table.Column<int>(type: "INTEGER", nullable: true),
                    WhitelistStatus = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    PreferredServerMachineId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    PreferredServerName = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    DiscordUserId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    DiscordUsername = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    DiscordDmOptIn = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserProfiles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UserProfiles_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Watchlist",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    MediaId = table.Column<int>(type: "INTEGER", nullable: false),
                    MediaType = table.Column<int>(type: "INTEGER", nullable: false),
                    UserId = table.Column<int>(type: "INTEGER", nullable: true),
                    Username = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    AddedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Watchlist", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Watchlist_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Notifications",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<int>(type: "INTEGER", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    Message = table.Column<string>(type: "TEXT", nullable: false),
                    Type = table.Column<int>(type: "INTEGER", nullable: false),
                    IsRead = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    RelatedRequestId = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Notifications", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Notifications_MediaRequests_RelatedRequestId",
                        column: x => x.RelatedRequestId,
                        principalTable: "MediaRequests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Notifications_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MediaRequests_MediaId_MediaType",
                table: "MediaRequests",
                columns: new[] { "MediaId", "MediaType" });

            migrationBuilder.CreateIndex(
                name: "IX_MediaRequests_RequestedAt",
                table: "MediaRequests",
                column: "RequestedAt");

            migrationBuilder.CreateIndex(
                name: "IX_MediaRequests_RequestedBy",
                table: "MediaRequests",
                column: "RequestedBy");

            migrationBuilder.CreateIndex(
                name: "IX_MediaRequests_RequestedByUserId",
                table: "MediaRequests",
                column: "RequestedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_MediaRequests_Status",
                table: "MediaRequests",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_CreatedAt",
                table: "Notifications",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_RelatedRequestId",
                table: "Notifications",
                column: "RelatedRequestId");

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_UserId",
                table: "Notifications",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_UserId_IsRead",
                table: "Notifications",
                columns: new[] { "UserId", "IsRead" });

            migrationBuilder.CreateIndex(
                name: "IX_PlexMappings_ExternalKey",
                table: "PlexMappings",
                column: "ExternalKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PlexMappings_RatingKey",
                table: "PlexMappings",
                column: "RatingKey");

            migrationBuilder.CreateIndex(
                name: "IX_UserProfiles_PlexUsername",
                table: "UserProfiles",
                column: "PlexUsername");

            migrationBuilder.CreateIndex(
                name: "IX_UserProfiles_UserId",
                table: "UserProfiles",
                column: "UserId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Users_Username",
                table: "Users",
                column: "Username",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Watchlist_MediaId",
                table: "Watchlist",
                column: "MediaId");

            migrationBuilder.CreateIndex(
                name: "IX_Watchlist_UserId_MediaId_MediaType",
                table: "Watchlist",
                columns: new[] { "UserId", "MediaId", "MediaType" });

            migrationBuilder.CreateIndex(
                name: "IX_Watchlist_Username",
                table: "Watchlist",
                column: "Username");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Notifications");

            migrationBuilder.DropTable(
                name: "PlexMappings");

            migrationBuilder.DropTable(
                name: "UserProfiles");

            migrationBuilder.DropTable(
                name: "Watchlist");

            migrationBuilder.DropTable(
                name: "MediaRequests");

            migrationBuilder.DropTable(
                name: "Users");
        }
    }
}

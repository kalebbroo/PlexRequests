using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PlexRequestsHosted.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddFirefoxBrowserCapture : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "FirefoxCapturePairings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    IndexerId = table.Column<int>(type: "INTEGER", nullable: false),
                    CodeHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UsedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FirefoxCapturePairings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FirefoxCapturePairings_Indexers_IndexerId",
                        column: x => x.IndexerId,
                        principalTable: "Indexers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "FirefoxCaptureDevices",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    PairingId = table.Column<Guid>(type: "TEXT", nullable: false),
                    IndexerId = table.Column<int>(type: "INTEGER", nullable: false),
                    TokenHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    ExtensionVersion = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastSeenAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastCaptureAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    BatchesReceived = table.Column<long>(type: "INTEGER", nullable: false),
                    ItemsReceived = table.Column<long>(type: "INTEGER", nullable: false),
                    LastParserVersion = table.Column<int>(type: "INTEGER", nullable: true),
                    LastPageUrl = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                    RevokedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FirefoxCaptureDevices", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FirefoxCaptureDevices_FirefoxCapturePairings_PairingId",
                        column: x => x.PairingId,
                        principalTable: "FirefoxCapturePairings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_FirefoxCaptureDevices_Indexers_IndexerId",
                        column: x => x.IndexerId,
                        principalTable: "Indexers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FirefoxCaptureDevices_IndexerId_RevokedAt_ExpiresAt",
                table: "FirefoxCaptureDevices",
                columns: new[] { "IndexerId", "RevokedAt", "ExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "IX_FirefoxCaptureDevices_PairingId",
                table: "FirefoxCaptureDevices",
                column: "PairingId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FirefoxCaptureDevices_TokenHash",
                table: "FirefoxCaptureDevices",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FirefoxCapturePairings_CodeHash",
                table: "FirefoxCapturePairings",
                column: "CodeHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FirefoxCapturePairings_ExpiresAt",
                table: "FirefoxCapturePairings",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_FirefoxCapturePairings_IndexerId",
                table: "FirefoxCapturePairings",
                column: "IndexerId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FirefoxCaptureDevices");

            migrationBuilder.DropTable(
                name: "FirefoxCapturePairings");
        }
    }
}

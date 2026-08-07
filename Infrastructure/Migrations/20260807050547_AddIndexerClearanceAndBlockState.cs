using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PlexRequestsHosted.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddIndexerClearanceAndBlockState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ClearanceCookieEncrypted",
                table: "Indexers",
                type: "TEXT",
                maxLength: 4096,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ClearanceObtainedAt",
                table: "Indexers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LastBlockReason",
                table: "Indexers",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastBlockedAt",
                table: "Indexers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "UserAgent",
                table: "Indexers",
                type: "TEXT",
                maxLength: 512,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ClearanceCookieEncrypted",
                table: "Indexers");

            migrationBuilder.DropColumn(
                name: "ClearanceObtainedAt",
                table: "Indexers");

            migrationBuilder.DropColumn(
                name: "LastBlockReason",
                table: "Indexers");

            migrationBuilder.DropColumn(
                name: "LastBlockedAt",
                table: "Indexers");

            migrationBuilder.DropColumn(
                name: "UserAgent",
                table: "Indexers");
        }
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PlexRequestsHosted.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddUserAccountLifecycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AccountStatus",
                table: "UserProfiles",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "AccountStatusChangedAt",
                table: "UserProfiles",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AccountStatusChangedBy",
                table: "UserProfiles",
                type: "TEXT",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AccountStatusReason",
                table: "UserProfiles",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "SuspendedUntil",
                table: "UserProfiles",
                type: "TEXT",
                nullable: true);

            // The legacy whitelist field was never enforced by the application, but preserve an explicit
            // Denied value as a disabled account instead of silently reactivating it during cleanup.
            migrationBuilder.Sql("""
                UPDATE "UserProfiles"
                SET "AccountStatus" = 2,
                    "AccountStatusReason" = 'Migrated from legacy denied status',
                    "AccountStatusChangedAt" = CURRENT_TIMESTAMP,
                    "AccountStatusChangedBy" = 'System · migration'
                WHERE lower("WhitelistStatus") = 'denied'
                """);

            migrationBuilder.DropColumn(
                name: "WhitelistStatus",
                table: "UserProfiles");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "WhitelistStatus",
                table: "UserProfiles",
                type: "TEXT",
                maxLength: 16,
                nullable: false,
                defaultValue: "Unknown");

            migrationBuilder.Sql("""
                UPDATE "UserProfiles"
                SET "WhitelistStatus" = CASE WHEN "AccountStatus" = 2 THEN 'Denied' ELSE 'Approved' END
                """);

            migrationBuilder.DropColumn(
                name: "AccountStatus",
                table: "UserProfiles");

            migrationBuilder.DropColumn(
                name: "AccountStatusChangedAt",
                table: "UserProfiles");

            migrationBuilder.DropColumn(
                name: "AccountStatusChangedBy",
                table: "UserProfiles");

            migrationBuilder.DropColumn(
                name: "AccountStatusReason",
                table: "UserProfiles");

            migrationBuilder.DropColumn(
                name: "SuspendedUntil",
                table: "UserProfiles");

        }
    }
}

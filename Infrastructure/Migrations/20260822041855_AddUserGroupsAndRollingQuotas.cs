using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PlexRequestsHosted.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddUserGroupsAndRollingQuotas : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "MovieRequestLimitDays",
                table: "UserProfiles",
                type: "INTEGER",
                nullable: false,
                defaultValue: 7);

            migrationBuilder.AddColumn<int>(
                name: "MusicRequestLimitDays",
                table: "UserProfiles",
                type: "INTEGER",
                nullable: false,
                defaultValue: 7);

            migrationBuilder.AddColumn<int>(
                name: "Permissions",
                table: "UserProfiles",
                type: "INTEGER",
                nullable: false,
                defaultValue: 7);

            migrationBuilder.AddColumn<int>(
                name: "TvRequestLimitDays",
                table: "UserProfiles",
                type: "INTEGER",
                nullable: false,
                defaultValue: 7);

            migrationBuilder.AddColumn<int>(
                name: "UserGroupId",
                table: "UserProfiles",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "UserGroups",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    Permissions = table.Column<int>(type: "INTEGER", nullable: false),
                    MovieRequestLimit = table.Column<int>(type: "INTEGER", nullable: true),
                    MovieRequestLimitDays = table.Column<int>(type: "INTEGER", nullable: false),
                    TvRequestLimit = table.Column<int>(type: "INTEGER", nullable: true),
                    TvRequestLimitDays = table.Column<int>(type: "INTEGER", nullable: false),
                    MusicRequestLimit = table.Column<int>(type: "INTEGER", nullable: true),
                    MusicRequestLimitDays = table.Column<int>(type: "INTEGER", nullable: false),
                    AllowedQualityProfileIdsCsv = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    IsSystem = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserGroups", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_UserProfiles_UserGroupId",
                table: "UserProfiles",
                column: "UserGroupId");

            migrationBuilder.CreateIndex(
                name: "IX_UserGroups_Name",
                table: "UserGroups",
                column: "Name",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_UserProfiles_UserGroups_UserGroupId",
                table: "UserProfiles",
                column: "UserGroupId",
                principalTable: "UserGroups",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_UserProfiles_UserGroups_UserGroupId",
                table: "UserProfiles");

            migrationBuilder.DropTable(
                name: "UserGroups");

            migrationBuilder.DropIndex(
                name: "IX_UserProfiles_UserGroupId",
                table: "UserProfiles");

            migrationBuilder.DropColumn(
                name: "MovieRequestLimitDays",
                table: "UserProfiles");

            migrationBuilder.DropColumn(
                name: "MusicRequestLimitDays",
                table: "UserProfiles");

            migrationBuilder.DropColumn(
                name: "Permissions",
                table: "UserProfiles");

            migrationBuilder.DropColumn(
                name: "TvRequestLimitDays",
                table: "UserProfiles");

            migrationBuilder.DropColumn(
                name: "UserGroupId",
                table: "UserProfiles");
        }
    }
}

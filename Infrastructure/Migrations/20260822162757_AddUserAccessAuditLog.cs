using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PlexRequestsHosted.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddUserAccessAuditLog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "UserAccessAudits",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TargetType = table.Column<int>(type: "INTEGER", nullable: false),
                    TargetId = table.Column<int>(type: "INTEGER", nullable: false),
                    TargetName = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Action = table.Column<int>(type: "INTEGER", nullable: false),
                    ActorUserId = table.Column<int>(type: "INTEGER", nullable: true),
                    ActorUsername = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Summary = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserAccessAudits", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_UserAccessAudits_ActorUserId_Id",
                table: "UserAccessAudits",
                columns: new[] { "ActorUserId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_UserAccessAudits_CreatedAt",
                table: "UserAccessAudits",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_UserAccessAudits_TargetType_TargetId_Id",
                table: "UserAccessAudits",
                columns: new[] { "TargetType", "TargetId", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "UserAccessAudits");
        }
    }
}

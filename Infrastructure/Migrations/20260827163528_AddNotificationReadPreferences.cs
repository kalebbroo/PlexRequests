using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PlexRequestsHosted.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddNotificationReadPreferences : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "NotificationAutoReadSeconds",
                table: "UserProfiles",
                type: "INTEGER",
                nullable: false,
                defaultValue: 30);

            migrationBuilder.AddColumn<int>(
                name: "NotificationReadBehavior",
                table: "UserProfiles",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "NotificationAutoReadSeconds",
                table: "UserProfiles");

            migrationBuilder.DropColumn(
                name: "NotificationReadBehavior",
                table: "UserProfiles");
        }
    }
}

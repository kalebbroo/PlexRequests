using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PlexRequestsHosted.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddOptInNotificationPreferences : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "DiscordNotificationTypes",
                table: "UserProfiles",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "WebNotificationTypes",
                table: "UserProfiles",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            // Older linking code enabled DMs automatically. That value cannot prove the user knowingly
            // opted in, so the privacy-safe migration resets it along with the new zero-valued masks.
            migrationBuilder.Sql("UPDATE UserProfiles SET DiscordDmOptIn = 0;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DiscordNotificationTypes",
                table: "UserProfiles");

            migrationBuilder.DropColumn(
                name: "WebNotificationTypes",
                table: "UserProfiles");
        }
    }
}

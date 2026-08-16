using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PlexRequestsHosted.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RemoveUnusedRssSweepInterval : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RssSweepIntervalSeconds",
                table: "MonitoringPreferences");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "RssSweepIntervalSeconds",
                table: "MonitoringPreferences",
                type: "INTEGER",
                nullable: false,
                defaultValue: 900);
        }
    }
}

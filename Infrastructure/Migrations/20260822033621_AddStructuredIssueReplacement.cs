using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PlexRequestsHosted.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddStructuredIssueReplacement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ReplacementApprovedAt",
                table: "MediaIssues",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReplacementApprovedBy",
                table: "MediaIssues",
                type: "TEXT",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ReplacementJobId",
                table: "MediaIssues",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ReplacementRequested",
                table: "MediaIssues",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsReplacement",
                table: "FulfillmentJobs",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "MediaIssueId",
                table: "FulfillmentJobs",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ReplacementApprovedAt",
                table: "MediaIssues");

            migrationBuilder.DropColumn(
                name: "ReplacementApprovedBy",
                table: "MediaIssues");

            migrationBuilder.DropColumn(
                name: "ReplacementJobId",
                table: "MediaIssues");

            migrationBuilder.DropColumn(
                name: "ReplacementRequested",
                table: "MediaIssues");

            migrationBuilder.DropColumn(
                name: "IsReplacement",
                table: "FulfillmentJobs");

            migrationBuilder.DropColumn(
                name: "MediaIssueId",
                table: "FulfillmentJobs");
        }
    }
}

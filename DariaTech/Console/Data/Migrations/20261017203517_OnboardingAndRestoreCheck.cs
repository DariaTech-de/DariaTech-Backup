using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DariaTech.Console.Data.Migrations
{
    /// <inheritdoc />
    public partial class OnboardingAndRestoreCheck : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "Created",
                table: "EnrollmentTokens",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.AddColumn<Guid>(
                name: "DeviceId",
                table: "EnrollmentTokens",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ExpectedDevices",
                table: "Customers",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "Checked",
                table: "Commands",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CheckedBy",
                table: "Commands",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Created",
                table: "EnrollmentTokens");

            migrationBuilder.DropColumn(
                name: "DeviceId",
                table: "EnrollmentTokens");

            migrationBuilder.DropColumn(
                name: "ExpectedDevices",
                table: "Customers");

            migrationBuilder.DropColumn(
                name: "Checked",
                table: "Commands");

            migrationBuilder.DropColumn(
                name: "CheckedBy",
                table: "Commands");
        }
    }
}

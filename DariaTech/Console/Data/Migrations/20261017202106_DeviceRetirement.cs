using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DariaTech.Console.Data.Migrations
{
    /// <inheritdoc />
    public partial class DeviceRetirement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "HandoverFromDeviceId",
                table: "ManagedJobs",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ReplacedByDeviceId",
                table: "Devices",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "RetireConfirmed",
                table: "Devices",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "RetireManuallyConfirmed",
                table: "Devices",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RetireManuallyConfirmedBy",
                table: "Devices",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "RetireRequested",
                table: "Devices",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "HandoverFromDeviceId",
                table: "ManagedJobs");

            migrationBuilder.DropColumn(
                name: "ReplacedByDeviceId",
                table: "Devices");

            migrationBuilder.DropColumn(
                name: "RetireConfirmed",
                table: "Devices");

            migrationBuilder.DropColumn(
                name: "RetireManuallyConfirmed",
                table: "Devices");

            migrationBuilder.DropColumn(
                name: "RetireManuallyConfirmedBy",
                table: "Devices");

            migrationBuilder.DropColumn(
                name: "RetireRequested",
                table: "Devices");
        }
    }
}

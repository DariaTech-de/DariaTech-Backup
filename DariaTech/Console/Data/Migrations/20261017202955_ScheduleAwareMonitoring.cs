using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DariaTech.Console.Data.Migrations
{
    /// <inheritdoc />
    public partial class ScheduleAwareMonitoring : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "KeepVersions",
                table: "ManagedJobs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "MaxBackupAgeHours",
                table: "ManagedJobs",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ScheduleDays",
                table: "ManagedJobs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "ScheduleRecorded",
                table: "ManagedJobs",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "ScheduleRepeatHours",
                table: "ManagedJobs",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AbsenceNote",
                table: "Devices",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "AbsentUntil",
                table: "Devices",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "KeepVersions",
                table: "ManagedJobs");

            migrationBuilder.DropColumn(
                name: "MaxBackupAgeHours",
                table: "ManagedJobs");

            migrationBuilder.DropColumn(
                name: "ScheduleDays",
                table: "ManagedJobs");

            migrationBuilder.DropColumn(
                name: "ScheduleRecorded",
                table: "ManagedJobs");

            migrationBuilder.DropColumn(
                name: "ScheduleRepeatHours",
                table: "ManagedJobs");

            migrationBuilder.DropColumn(
                name: "AbsenceNote",
                table: "Devices");

            migrationBuilder.DropColumn(
                name: "AbsentUntil",
                table: "Devices");
        }
    }
}

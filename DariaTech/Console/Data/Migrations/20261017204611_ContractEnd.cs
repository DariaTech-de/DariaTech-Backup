using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DariaTech.Console.Data.Migrations
{
    /// <inheritdoc />
    public partial class ContractEnd : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "AccessLocked",
                table: "Customers",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ArchiveDeletionApproved",
                table: "Customers",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ArchiveDeletionApprovedBy",
                table: "Customers",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "Closed",
                table: "Customers",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "ContractEnd",
                table: "Customers",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "HandoverNote",
                table: "Customers",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateOnly>(
                name: "RetainArchivesUntil",
                table: "Customers",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "StorageAccessRevoked",
                table: "Customers",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StorageAccessRevokedBy",
                table: "Customers",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AccessLocked",
                table: "Customers");

            migrationBuilder.DropColumn(
                name: "ArchiveDeletionApproved",
                table: "Customers");

            migrationBuilder.DropColumn(
                name: "ArchiveDeletionApprovedBy",
                table: "Customers");

            migrationBuilder.DropColumn(
                name: "Closed",
                table: "Customers");

            migrationBuilder.DropColumn(
                name: "ContractEnd",
                table: "Customers");

            migrationBuilder.DropColumn(
                name: "HandoverNote",
                table: "Customers");

            migrationBuilder.DropColumn(
                name: "RetainArchivesUntil",
                table: "Customers");

            migrationBuilder.DropColumn(
                name: "StorageAccessRevoked",
                table: "Customers");

            migrationBuilder.DropColumn(
                name: "StorageAccessRevokedBy",
                table: "Customers");
        }
    }
}

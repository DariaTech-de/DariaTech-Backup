using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DariaTech.Console.Data.Migrations
{
    /// <inheritdoc />
    public partial class ManagedJobUnreportedSince : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "UnreportedSince",
                table: "ManagedJobs",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "UnreportedSince",
                table: "ManagedJobs");
        }
    }
}

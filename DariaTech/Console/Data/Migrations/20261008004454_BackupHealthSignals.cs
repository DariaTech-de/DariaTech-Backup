using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DariaTech.Console.Data.Migrations
{
    /// <inheritdoc />
    public partial class BackupHealthSignals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "QuotaError",
                table: "Runs",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "QuotaFreeBytes",
                table: "Runs",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "QuotaTotalBytes",
                table: "Runs",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "QuotaWarning",
                table: "Runs",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "RetentionError",
                table: "Runs",
                type: "boolean",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "QuotaError",
                table: "Runs");

            migrationBuilder.DropColumn(
                name: "QuotaFreeBytes",
                table: "Runs");

            migrationBuilder.DropColumn(
                name: "QuotaTotalBytes",
                table: "Runs");

            migrationBuilder.DropColumn(
                name: "QuotaWarning",
                table: "Runs");

            migrationBuilder.DropColumn(
                name: "RetentionError",
                table: "Runs");
        }
    }
}

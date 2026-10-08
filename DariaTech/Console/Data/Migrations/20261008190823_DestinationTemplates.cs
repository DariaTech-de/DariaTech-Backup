using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DariaTech.Console.Data.Migrations
{
    /// <inheritdoc />
    public partial class DestinationTemplates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DestinationTemplates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    TargetUrl = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    EncryptedOptions = table.Column<string>(type: "character varying(120000)", maxLength: 120000, nullable: false),
                    IsDefault = table.Column<bool>(type: "boolean", nullable: false),
                    Created = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DestinationTemplates", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DestinationTemplates_IsDefault",
                table: "DestinationTemplates",
                column: "IsDefault",
                unique: true,
                filter: "\"IsDefault\"");

            migrationBuilder.CreateIndex(
                name: "IX_DestinationTemplates_Name",
                table: "DestinationTemplates",
                column: "Name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DestinationTemplates");
        }
    }
}

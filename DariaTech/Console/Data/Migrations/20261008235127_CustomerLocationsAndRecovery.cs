using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DariaTech.Console.Data.Migrations
{
    /// <inheritdoc />
    public partial class CustomerLocationsAndRecovery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_DestinationTemplates_IsDefault",
                table: "DestinationTemplates");

            migrationBuilder.DropIndex(
                name: "IX_DestinationTemplates_Name",
                table: "DestinationTemplates");

            migrationBuilder.AddColumn<bool>(
                name: "RestoreOnly",
                table: "ManagedJobs",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "SourceManagedJobId",
                table: "ManagedJobs",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "DestinationTemplates",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_DestinationTemplates_TenantId",
                table: "DestinationTemplates",
                column: "TenantId",
                unique: true,
                filter: "\"IsDefault\"")
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "IX_DestinationTemplates_TenantId_Name",
                table: "DestinationTemplates",
                columns: new[] { "TenantId", "Name" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.AddForeignKey(
                name: "FK_DestinationTemplates_Tenants_TenantId",
                table: "DestinationTemplates",
                column: "TenantId",
                principalTable: "Tenants",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_DestinationTemplates_Tenants_TenantId",
                table: "DestinationTemplates");

            migrationBuilder.DropIndex(
                name: "IX_DestinationTemplates_TenantId",
                table: "DestinationTemplates");

            migrationBuilder.DropIndex(
                name: "IX_DestinationTemplates_TenantId_Name",
                table: "DestinationTemplates");

            migrationBuilder.DropColumn(
                name: "RestoreOnly",
                table: "ManagedJobs");

            migrationBuilder.DropColumn(
                name: "SourceManagedJobId",
                table: "ManagedJobs");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "DestinationTemplates");

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
    }
}

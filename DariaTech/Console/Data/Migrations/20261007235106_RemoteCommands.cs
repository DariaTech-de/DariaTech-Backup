using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DariaTech.Console.Data.Migrations
{
    /// <inheritdoc />
    public partial class RemoteCommands : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Commands",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DeviceId = table.Column<Guid>(type: "uuid", nullable: false),
                    JobId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestedBy = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    ApprovedBy = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    EncryptedPayload = table.Column<string>(type: "character varying(120000)", maxLength: 120000, nullable: false),
                    Signature = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    Action = table.Column<int>(type: "integer", nullable: false),
                    Expires = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Status = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    TaskId = table.Column<long>(type: "bigint", nullable: true),
                    ErrorCode = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Commands", x => x.Id);
                    table.UniqueConstraint("AK_Commands_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.ForeignKey(
                        name: "FK_Commands_Devices_TenantId_DeviceId",
                        columns: x => new { x.TenantId, x.DeviceId },
                        principalTable: "Devices",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Commands_Jobs_TenantId_JobId",
                        columns: x => new { x.TenantId, x.JobId },
                        principalTable: "Jobs",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Commands_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Commands_DeviceId_Status_Expires",
                table: "Commands",
                columns: new[] { "DeviceId", "Status", "Expires" });

            migrationBuilder.CreateIndex(
                name: "IX_Commands_TenantId_DeviceId",
                table: "Commands",
                columns: new[] { "TenantId", "DeviceId" });

            migrationBuilder.CreateIndex(
                name: "IX_Commands_TenantId_JobId",
                table: "Commands",
                columns: new[] { "TenantId", "JobId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Commands");
        }
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DariaTech.Console.Data.Migrations
{
    /// <inheritdoc />
    public partial class ApprovedAgentUpdates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AgentReleases",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Sequence = table.Column<long>(type: "bigint", nullable: false),
                    Version = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    Payload = table.Column<string>(type: "character varying(120000)", maxLength: 120000, nullable: false),
                    Signature = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    Expires = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentReleases", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "UpdateDeployments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DeviceId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReleaseId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    ErrorCode = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Approved = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UpdateDeployments", x => x.Id);
                    table.UniqueConstraint("AK_UpdateDeployments_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.ForeignKey(
                        name: "FK_UpdateDeployments_AgentReleases_ReleaseId",
                        column: x => x.ReleaseId,
                        principalTable: "AgentReleases",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_UpdateDeployments_Devices_TenantId_DeviceId",
                        columns: x => new { x.TenantId, x.DeviceId },
                        principalTable: "Devices",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_UpdateDeployments_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AgentReleases_Sequence",
                table: "AgentReleases",
                column: "Sequence",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UpdateDeployments_DeviceId_ReleaseId",
                table: "UpdateDeployments",
                columns: new[] { "DeviceId", "ReleaseId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UpdateDeployments_ReleaseId",
                table: "UpdateDeployments",
                column: "ReleaseId");

            migrationBuilder.CreateIndex(
                name: "IX_UpdateDeployments_TenantId_DeviceId",
                table: "UpdateDeployments",
                columns: new[] { "TenantId", "DeviceId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "UpdateDeployments");

            migrationBuilder.DropTable(
                name: "AgentReleases");
        }
    }
}

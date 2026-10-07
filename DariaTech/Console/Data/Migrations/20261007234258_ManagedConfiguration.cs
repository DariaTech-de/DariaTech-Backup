using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DariaTech.Console.Data.Migrations
{
    /// <inheritdoc />
    public partial class ManagedConfiguration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ManagedJobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DeviceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    LatestRevision = table.Column<long>(type: "bigint", nullable: false),
                    AppliedRevision = table.Column<long>(type: "bigint", nullable: false),
                    LastReportedRevision = table.Column<long>(type: "bigint", nullable: false),
                    LocalJobId = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Status = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ManagedJobs", x => x.Id);
                    table.UniqueConstraint("AK_ManagedJobs_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.ForeignKey(
                        name: "FK_ManagedJobs_Devices_TenantId_DeviceId",
                        columns: x => new { x.TenantId, x.DeviceId },
                        principalTable: "Devices",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ManagedJobs_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ConfigurationRevisions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ManagedJobId = table.Column<Guid>(type: "uuid", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    EncryptedConfiguration = table.Column<string>(type: "character varying(120000)", maxLength: 120000, nullable: false),
                    Created = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConfigurationRevisions", x => x.Id);
                    table.UniqueConstraint("AK_ConfigurationRevisions_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.ForeignKey(
                        name: "FK_ConfigurationRevisions_ManagedJobs_TenantId_ManagedJobId",
                        columns: x => new { x.TenantId, x.ManagedJobId },
                        principalTable: "ManagedJobs",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ConfigurationRevisions_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ConfigurationRevisions_ManagedJobId_Revision",
                table: "ConfigurationRevisions",
                columns: new[] { "ManagedJobId", "Revision" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ConfigurationRevisions_TenantId_ManagedJobId",
                table: "ConfigurationRevisions",
                columns: new[] { "TenantId", "ManagedJobId" });

            migrationBuilder.Sql("""
                CREATE FUNCTION public.dariatech_configuration_immutable() RETURNS trigger
                LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'Configuration revisions are immutable'; END; $$;
                CREATE TRIGGER configuration_immutable BEFORE UPDATE OR DELETE ON "ConfigurationRevisions"
                FOR EACH ROW EXECUTE FUNCTION public.dariatech_configuration_immutable();
                """);
            migrationBuilder.CreateIndex(
                name: "IX_ManagedJobs_TenantId_DeviceId",
                table: "ManagedJobs",
                columns: new[] { "TenantId", "DeviceId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER configuration_immutable ON \"ConfigurationRevisions\"; DROP FUNCTION public.dariatech_configuration_immutable();");
            migrationBuilder.DropTable(
                name: "ConfigurationRevisions");

            migrationBuilder.DropTable(
                name: "ManagedJobs");
        }
    }
}

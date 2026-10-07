using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DariaTech.Console.Data.Migrations
{
    /// <inheritdoc />
    public partial class AuditIntegrityAndProgress : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE FUNCTION public.dariatech_audit_append_only() RETURNS trigger
                LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'Audit events are append-only'; END; $$;
                CREATE TRIGGER audit_append_only BEFORE UPDATE OR DELETE ON "Audit"
                FOR EACH ROW EXECUTE FUNCTION public.dariatech_audit_append_only();
                """);
            migrationBuilder.AddColumn<string>(
                name: "ActiveJobLocalId",
                table: "Devices",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "ActiveTaskId",
                table: "Devices",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "Progress",
                table: "Devices",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "ProgressBytes",
                table: "Devices",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "ProgressFiles",
                table: "Devices",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddCheckConstraint(
                name: "backup_run_statistics",
                table: "Runs",
                sql: "\"Bytes\" >= 0 AND \"Files\" >= 0 AND \"StorageBytes\" >= 0 AND (\"Completed\" IS NULL OR \"Completed\" >= \"Started\")");

            migrationBuilder.CreateIndex(
                name: "IX_Audit_TenantId",
                table: "Audit",
                column: "TenantId");

            migrationBuilder.AddForeignKey(
                name: "FK_Audit_Tenants_TenantId",
                table: "Audit",
                column: "TenantId",
                principalTable: "Tenants",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER audit_append_only ON \"Audit\"; DROP FUNCTION public.dariatech_audit_append_only();");
            migrationBuilder.DropForeignKey(
                name: "FK_Audit_Tenants_TenantId",
                table: "Audit");

            migrationBuilder.DropCheckConstraint(
                name: "backup_run_statistics",
                table: "Runs");

            migrationBuilder.DropIndex(
                name: "IX_Audit_TenantId",
                table: "Audit");

            migrationBuilder.DropColumn(
                name: "ActiveJobLocalId",
                table: "Devices");

            migrationBuilder.DropColumn(
                name: "ActiveTaskId",
                table: "Devices");

            migrationBuilder.DropColumn(
                name: "Progress",
                table: "Devices");

            migrationBuilder.DropColumn(
                name: "ProgressBytes",
                table: "Devices");

            migrationBuilder.DropColumn(
                name: "ProgressFiles",
                table: "Devices");
        }
    }
}

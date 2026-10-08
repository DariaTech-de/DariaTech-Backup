using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DariaTech.Console.Data.Migrations
{
    /// <inheritdoc />
    public partial class PrivacyErasureAuthority : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION public.dariatech_configuration_immutable() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                  IF TG_OP = 'DELETE'
                    AND current_setting('dariatech.erase_tenant', true) = OLD."TenantId"::text
                    AND current_user = pg_get_userbyid((SELECT relowner FROM pg_class WHERE oid='public."ConfigurationRevisions"'::regclass))
                  THEN RETURN OLD;
                  END IF;
                  RAISE EXCEPTION 'Configuration revisions are immutable';
                END; $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION public.dariatech_configuration_immutable() RETURNS trigger
                LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'Configuration revisions are immutable'; END; $$;
                """);
        }
    }
}

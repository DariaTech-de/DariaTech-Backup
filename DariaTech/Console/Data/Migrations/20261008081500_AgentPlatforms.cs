using Microsoft.EntityFrameworkCore.Migrations;
#nullable disable
namespace DariaTech.Console.Data.Migrations;
public partial class AgentPlatforms:Migration
{
 protected override void Up(MigrationBuilder migrationBuilder)
 {
  migrationBuilder.AddColumn<string>(name:"Platform",table:"Agents",type:"character varying(1000)",maxLength:1000,nullable:false,defaultValue:"");
  migrationBuilder.AddColumn<string>(name:"Platform",table:"AgentReleases",type:"character varying(1000)",maxLength:1000,nullable:false,defaultValue:"win-x64");
  migrationBuilder.DropIndex(name:"IX_AgentReleases_Sequence",table:"AgentReleases");
  migrationBuilder.CreateIndex(name:"IX_AgentReleases_Platform_Sequence",table:"AgentReleases",columns:new[]{"Platform","Sequence"},unique:true);
 }
 protected override void Down(MigrationBuilder migrationBuilder)
 {
  // Per-platform sequences can overlap. A downgrade is deliberately rejected if
  // it would destroy this distinction; operators must retire conflicting releases.
  migrationBuilder.Sql("DO $$ BEGIN IF EXISTS (SELECT 1 FROM \"AgentReleases\" GROUP BY \"Sequence\" HAVING COUNT(*)>1) THEN RAISE EXCEPTION 'Retire overlapping platform releases before downgrade'; END IF; END $$;");
  migrationBuilder.DropIndex(name:"IX_AgentReleases_Platform_Sequence",table:"AgentReleases");
  migrationBuilder.CreateIndex(name:"IX_AgentReleases_Sequence",table:"AgentReleases",column:"Sequence",unique:true);
  migrationBuilder.DropColumn(name:"Platform",table:"Agents");
  migrationBuilder.DropColumn(name:"Platform",table:"AgentReleases");
 }
}

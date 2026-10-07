using DariaTech.Console.Data;
using DariaTech.Console.Services;
using Microsoft.EntityFrameworkCore;
namespace DariaTech.Console.Api;
public sealed record NotificationInput(Guid TenantId,string Recipient,int RepeatMinutes,bool Enabled);
public static class NotificationApi
{
 public static void MapNotificationApi(this WebApplication app)
 {
  var g=app.MapGroup("/api/v1/management").RequireAuthorization();
  g.MapGet("/notification-rules",async(ManagementDb db)=>await db.NotificationRules.ToListAsync());
  g.MapPost("/notification-rules",async(NotificationInput input,ManagementDb db,HttpContext ctx)=>
  {
   if(!NotificationOptions.ValidAddress(input.Recipient)||input.RepeatMinutes is <60 or >43200)return Results.BadRequest();
   if(!await db.Customers.AnyAsync(x=>x.TenantId==input.TenantId&&x.Active))return Results.NotFound();
   var rule=new NotificationRule{TenantId=input.TenantId,Recipient=input.Recipient,RepeatMinutes=input.RepeatMinutes,Enabled=input.Enabled};db.NotificationRules.Add(rule);ManagementApi.Audit(db,ctx,rule.TenantId,"notification.rule-created",rule.Id);await db.SaveChangesAsync();return Results.Ok(rule);
  }).RequireAuthorization("Admin");
  g.MapPut("/notification-rules/{id:guid}",async(Guid id,NotificationInput input,ManagementDb db,HttpContext ctx)=>
  {
   if(!NotificationOptions.ValidAddress(input.Recipient)||input.RepeatMinutes is <60 or >43200)return Results.BadRequest();
   var rule=await db.NotificationRules.SingleOrDefaultAsync(x=>x.Id==id&&x.TenantId==input.TenantId);if(rule is null)return Results.NotFound();
   rule.Recipient=input.Recipient;rule.RepeatMinutes=input.RepeatMinutes;rule.Enabled=input.Enabled;ManagementApi.Audit(db,ctx,rule.TenantId,"notification.rule-changed",rule.Id);await db.SaveChangesAsync();return Results.Ok(rule);
  }).RequireAuthorization("Admin");
  g.MapGet("/notification-deliveries",async(ManagementDb db)=>await db.Deliveries.OrderByDescending(x=>x.Created).Take(500).ToListAsync());
 }
}

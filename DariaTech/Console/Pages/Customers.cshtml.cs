using DariaTech.Console.Data;
using DariaTech.Console.Services;
using DariaTech.Console.Api;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
namespace DariaTech.Console.Pages;
public sealed class CustomersModel(ManagementDb db,IOptions<MonitoringOptions> options):PageModel
{
 public List<Customer> Customers{get;private set;}=[];public List<DeviceRow> Rows{get;private set;}=[];
 [BindProperty]public CustomerForm Input{get;set;}=new();
 public async Task OnGetAsync(){Customers=await db.Customers.OrderBy(x=>x.Name).ToListAsync();Rows=await Overview.Load(db,options.Value);}
 public async Task<IActionResult> OnPostAsync()
 {
  if(!User.IsInRole("SuperAdmin")&&!User.IsInRole("Administrator"))return Forbid();
  if(!ModelState.IsValid||!ManagementApi.Text(Input.Name,200)||!ManagementApi.Text(Input.Number,80)||new[]{Input.Contact,Input.Email,Input.Phone,Input.Address,Input.Notes}.Any(x=>(x?.Length??0)>1000)){ModelState.AddModelError("","Bitte gültige Kundendaten eingeben.");await OnGetAsync();return Page();}
  if(await db.Customers.AnyAsync(x=>x.Number==Input.Number)){ModelState.AddModelError("","Kundennummer existiert bereits.");await OnGetAsync();return Page();}
  var tenant=new Tenant{Name=Input.Name};db.Tenants.Add(tenant);var customer=new Customer{TenantId=tenant.Id,Name=Input.Name,Number=Input.Number,Contact=Input.Contact??"",Email=Input.Email??"",Phone=Input.Phone??"",Address=Input.Address??"",Notes=Input.Notes??""};db.Customers.Add(customer);ManagementApi.Audit(db,HttpContext,tenant.Id,"customer.created",customer.Id);await db.SaveChangesAsync();return RedirectToPage("/Customer",new{id=customer.Id});
 }
}

public sealed class CustomerForm
{
 [System.ComponentModel.DataAnnotations.Required]public string Name{get;set;}="";
 [System.ComponentModel.DataAnnotations.Required]public string Number{get;set;}="";
 public string? Contact{get;set;} public string? Email{get;set;} public string? Phone{get;set;}
 public string? Address{get;set;} public string? Notes{get;set;}
}

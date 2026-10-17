using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
namespace DariaTech.Console.Pages;
[Authorize(Policy="Operator")]
public sealed class AgentRemovalModel:PageModel{public void OnGet(){}}

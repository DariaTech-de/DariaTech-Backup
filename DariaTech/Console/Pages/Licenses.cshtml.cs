using Microsoft.AspNetCore.Mvc.RazorPages;
namespace DariaTech.Console.Pages;
public sealed class LicensesModel(IWebHostEnvironment env):PageModel
{public List<string> Libraries{get;private set;}=[];public void OnGet(){var root=Path.Combine(env.WebRootPath,"legal","thirdparty");if(Directory.Exists(root))Libraries=Directory.GetDirectories(root).Select(Path.GetFileName).OfType<string>().Order().ToList();}}

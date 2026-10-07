using DariaTech.Console.Services;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
namespace DariaTech.Console.Pages;
public sealed class SettingsModel(IOptions<MonitoringOptions> options):PageModel { public MonitoringOptions Options=>options.Value; }

using System.Text.Encodings.Web;
using System.Text.Json;
using Duplicati.Library.Backends;
using Duplicati.Library.Interface;

// Destinations offered for central configuration. Excluded on purpose:
//  - rclone: its options name local executables/config files, i.e. remote code execution via configuration.
//  - duplicati: Duplicati Inc's hosted service, needs a separate commercial account and API key flow.
//  - cifs: deprecated alias of smb.
var excludedBackends = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "rclone", "duplicati", "cifs" };
// Options that read local files, execute programs, write diagnostics or weaken transport security never come from the Console.
var excludedOptions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
 "ssh-keyfile", "ssh-accept-any-fingerprints", "service-account-file", "gcs-service-account-file", "ignore-revocation-failure", "debug-propfind-file",
 "ftp-log-to-console", "ftp-log-privateinfo-to-console", "ftp-log-diagnostics", "accept-any-ssl-certificate",
 "accept-specified-ssl-hash", "oauth-url", "rclone-executable", "rclone-option", "rclone-local-repository",
};
var untested = BackendModules.UntestedBackendModules;
var deprecated = BackendModules.DeprecatedBackendModules;
var list = BackendModules.BuiltInBackendModules
 .Where(b => !excludedBackends.Contains(b.ProtocolKey))
 .OrderBy(b => b.DisplayName, StringComparer.OrdinalIgnoreCase)
 .Select(b => new {
  key = b.ProtocolKey,
  name = b.DisplayName,
  description = b.Description,
  deprecated = deprecated.Contains(b.ProtocolKey),
  untested = untested.Contains(b.ProtocolKey),
  options = (b.SupportedCommands ?? []).Where(o => !o.Deprecated && !excludedOptions.Contains(o.Name))
   .GroupBy(o => o.Name, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
   .OrderBy(o => o.Name, StringComparer.Ordinal)
   .Select(o => new {
    name = o.Name,
    type = o.Type.ToString(),
    secret = o.Type == CommandLineArgument.ArgumentType.Password,
    summary = o.ShortDescription,
    values = o.ValidValues is { Length: > 0 } v ? v : null,
    defaultValue = string.IsNullOrEmpty(o.DefaultValue) ? null : o.DefaultValue,
   }).ToArray(),
 }).ToArray();
var json = JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull }) + "\n";
var output = args.Length == 1 ? args[0] : throw new ArgumentException("Usage: DariaTech.CatalogGen OUTPUT_JSON");
File.WriteAllText(output, json);
Console.WriteLine($"{list.Length} destinations written to {output}");

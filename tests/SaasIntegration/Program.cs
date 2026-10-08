using Duplicati.Library.Interface;
using Duplicati.Proprietary.LicenseChecker;
using OfficeSource = Duplicati.Proprietary.Office365.SourceProvider;
using GoogleSource = Duplicati.Proprietary.GoogleWorkspace.SourceProvider;
using OfficeRestore = Duplicati.Proprietary.Office365.RestoreProvider;
using GoogleRestore = Duplicati.Proprietary.GoogleWorkspace.RestoreProvider;

// Development-only harness. No provider credentials, entitlement overrides or live writes.
Environment.SetEnvironmentVariable("DO_NOT_TRACK", "1");
Environment.SetEnvironmentVariable("USAGEREPORTER_Duplicati_LEVEL", "none");
Environment.SetEnvironmentVariable("AUTOUPDATER_Duplicati_SKIP_UPDATE", "1");
if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DUPLICATI_LICENSE_KEY")) ||
    File.Exists(Path.Combine(AppContext.BaseDirectory, "license.key")))
    throw new InvalidOperationException("Offline tests require an isolated environment without a license key.");

var passed = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException(name);
    passed++;
    Console.WriteLine("PASS " + name);
}
async Task Reject(Func<Task> operation, string name, Type expected)
{
    var rejected = false;
    try { await operation(); }
    catch (Exception error) { rejected = error.GetType() == expected; } // Never log raw provider exceptions.
    Check(rejected, name);
}
void Password(IList<ICommandLineArgument> commands, string name)
    => Check(commands.Single(x => x.Name == name).Type == CommandLineArgument.ArgumentType.Password,
        name + " is classified as a secret");

using var office = new OfficeSource();
using var google = new GoogleSource();
Check(office.Key == "office365" && google.Key == "googleworkspace",
    "Original source providers load with stable module identifiers");
Check(office.NeedsStoredMetadata && google.NeedsStoredMetadata,
    "SaaS sources declare their metadata requirement");
Check(new OfficeRestore().Key == office.Key && new GoogleRestore().Key == google.Key,
    "Original restore providers match the backup modules");
Password(office.SupportedCommands, "office365-client-secret");
Password(office.SupportedCommands, "office365-certificate-password");
Password(google.SupportedCommands, "google-client-secret");
Password(google.SupportedCommands, "google-refresh-token");
Password(google.SupportedCommands, "google-service-account-json");
await Reject(() => office.InitializeAsync(CancellationToken.None),
    "Office365 initialization rejects missing metadata configuration before authentication", typeof(UserInformationException));
await Reject(() => google.InitializeAsync(CancellationToken.None),
    "Google initialization rejects missing metadata configuration", typeof(UserInformationException));

await Reject(() =>
{
    using var invalid = new OfficeSource("office365://", "/offline-test", new());
    return Task.CompletedTask;
}, "Office365 refuses missing application credentials", typeof(UserInformationException));
using var unconfiguredGoogle = new GoogleSource("googleworkspace://", "/offline-test",
    new() { ["machine-id"] = "isolated-development-test",
            ["store-metadata-content-in-database"] = "true" });
await Reject(() => unconfiguredGoogle.TestAsync(CancellationToken.None),
    "Google connectivity refuses missing credentials without a successful result", typeof(Exception));

// Upstream deliberately grants five development seats without a key. Preserve this
// behavior; it is NOT evidence of a purchased entitlement or permission for production.
Check(LicenseHelper.LicenseData is null, "No purchased entitlement is invented");
Check(LicenseHelper.AvailableOffice365UserSeats == 5 &&
      LicenseHelper.AvailableGoogleWorkspaceUserSeats == 5,
    "Original limited development-seat behavior is preserved");
Console.WriteLine($"Passed {passed} offline checks. Live backup/restore NOT tested.");

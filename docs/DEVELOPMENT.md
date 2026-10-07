# Development

SDK is pinned in global.json to .NET 10.0.401 (latestPatch, no preview). Duplicati.slnx remains upstream; DariaTech.slnx builds the management layer independently. DariaTech modules live outside upstream core. No .NET SDK was initially installed in the execution environment; a pinned SDK was installed locally for validation.

```sh
scripts/build-duplicati.sh
python3 scripts/generate-branding.py --check
dotnet restore DariaTech.slnx --locked-mode
dotnet build DariaTech.slnx -c Release --no-restore
```

Management dependencies use committed NuGet lock files. To intentionally change a dependency, update csproj, restore to regenerate locks, run license collector and review the dependency diff. Branding changes go ONLY in branding/product.json and branding assets, then run generate-branding.py; generated copies must not drift.

## Tests

Use a dedicated disposable PostgreSQL database, never a production DSN. Export DARIATECH_TEST_DATABASE through a protected test environment. PostgresTests apply explicit migrations to that dedicated database and insert synthetic fixtures. They exercise real password/TOTP login and agent credentials, not bypass authentication.

```sh
dotnet test DariaTech/Tests/DariaTech.Tests.csproj -c Release
# Enable the real encrypted engine backup/restore test too:
export DARIATECH_ENGINE_SERVER_DLL="$PWD/Executables/Duplicati.Server/bin/Release/net10.0/Duplicati.Server.dll"
dotnet test DariaTech/Tests/DariaTech.Tests.csproj -c Release
```

Without engine DLL, that test is explicitly skipped. Without a test PostgreSQL DSN, DB tests fail setup; they are never silently converted to in-memory tests. The engine fixture uses generated secrets, encrypted local storage, actual engine API task execution and byte-for-byte restore verification. It does not expose test credentials in outputs.

Schema changes: create/review EF migration and snapshot, test upgrade, then run `--migrate` as an explicit operator command. Do not use EnsureCreated or automatic startup migration in production. CI must run the OSS engine checks and management security tests against PostgreSQL. The Windows installer workflow tests SYSTEM provisioning, DPAPI/ACL isolation, enrollment, real engine authentication, heartbeat, upgrade, restart and uninstall. Windows VSS/reboot and signed installer/update validation remain release gates.

## Status

See IMPLEMENTATION_PLAN.md for checked delivery versus later phases. Do not make partially implemented remote controls appear active. Console UI uses actual persisted data, including clear empty/unknown/no-backup states. Test fixture records are not product seed/demo data. Use logical commits with build/test evidence; do not commit local secrets, databases or test logs.

# Baseline build evidence

2026-10-07, Linux x64, .NET SDK 10.0.401, upstream a3061c912.

- OSS entrypoint: `dotnet build Executables/Duplicati.Server/Duplicati.Server.csproj -c Release -p:DariaTechOssOnly=true`
- Focused upstream NUnit selection: HasherTests, SensitiveDataFilterTests, LoginAttemptThrottleTests, BackendLoaderSchemeTests, RestoreHandlerTests, RestorePathTraversalTests.
- Result: 66 passed, 3 skipped, 0 failed; skips are existing upstream abort/stop restore tests. They are not counted as verified.
- The standard complete solution includes proprietary projects and is not the DariaTech distribution entrypoint.
- Added explicit Utility reference to SourceProviders, which previously obtained it through the proprietary loader.
- OSS guards also cover proprietary license DTOs/system-info and vendor-license synchronization; absence is reported as null, not a fake valid subscription.
- Existing nullable warnings in upstream tests remain upstream warnings. DariaTech projects use warnings as errors.

Build/test transcripts remain local artifacts; repeat with `scripts/build-duplicati.sh`. Before release also run Windows service/VSS/installer tests and the full upstream integration matrix.

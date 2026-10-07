#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
export DO_NOT_TRACK=1 DOTNET_CLI_TELEMETRY_OPTOUT=1
export AUTOUPDATER_Duplicati_SKIP_UPDATE=1
configuration="${CONFIGURATION:-Release}"
# Building the solution would explicitly include proprietary projects. Use these entry points.
dotnet restore Executables/Duplicati.Server/Duplicati.Server.csproj -p:DariaTechOssOnly=true
dotnet build Executables/Duplicati.Server/Duplicati.Server.csproj -c "$configuration" --no-restore -p:DariaTechOssOnly=true
dotnet test Duplicati/UnitTest/Duplicati.UnitTest.csproj -c "$configuration" -p:DariaTechOssOnly=true --filter "FullyQualifiedName~HasherTests|FullyQualifiedName~SensitiveDataFilterTests|FullyQualifiedName~LoginAttemptThrottleTests|FullyQualifiedName~BackendLoaderSchemeTests|FullyQualifiedName~RestoreHandlerTests|FullyQualifiedName~RestorePathTraversalTests"

param([string]$Compiler="${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",[ValidatePattern('^\d+\.\d+\.\d+$')][string]$VersionOverride)
$ErrorActionPreference='Stop'
Set-Location (Join-Path $PSScriptRoot '..')
$brand=Get-Content branding/product.json -Raw | ConvertFrom-Json
foreach ($value in $brand.PSObject.Properties.Value) { if ($value -is [string] -and ($value.Contains('"') -or $value.Contains("`n"))) { throw 'Unsafe branding value' } }
# Publish does not remove obsolete files. Recreate only these generated payload directories
# so a prior full/proprietary publish can never leak stale binaries into the OSS installer.
foreach ($generated in 'artifacts/windows/agent','artifacts/windows/engine') {
 if(Test-Path $generated) {
  if((Get-Item $generated).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Generated payload directory cannot be a link'}
  Remove-Item $generated -Recurse -Force
 }
}
$version=([xml](Get-Content DariaTech/Agent/DariaTech.Agent.csproj)).Project.PropertyGroup.Version
if ($VersionOverride) { $version=$VersionOverride }
dotnet publish DariaTech/Agent/DariaTech.Agent.csproj -c Release -r win-x64 --self-contained true -p:Version=$version -o artifacts/windows/agent
if ($LASTEXITCODE) { throw 'Agent publish failed' }
dotnet publish Executables/Duplicati.Server/Duplicati.Server.csproj -c Release -r win-x64 --self-contained true -p:DariaTechOssOnly=true -o artifacts/windows/engine
if ($LASTEXITCODE) { throw 'OSS engine publish failed' }
# Devices are managed from the Console only: the packaged engine serves a notice instead of the Duplicati web
# interface (with its remote-control and update settings). Only packaged files change, not engine code.
Remove-Item artifacts/windows/engine/webroot -Recurse -Force
Copy-Item DariaTech/Agent/local-ui artifacts/windows/engine/webroot -Recurse
$restricted=Get-ChildItem artifacts/windows/engine -Recurse -File | Where-Object { $_.Name -match '(?i)(Proprietary|Office365|GoogleWorkspace|DiskImage).*\.(dll|exe)$' }
if($restricted){throw 'Subscription-restricted binaries found in OSS payload'}
$cache=(dotnet nuget locals global-packages --list) -replace '^global-packages:\s*',''
foreach ($package in 'microsoft.netcore.app.runtime.win-x64','microsoft.aspnetcore.app.runtime.win-x64') {
 $root=Join-Path $cache $package
 $versionDirectory=Get-ChildItem $root -Directory | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
 if (!$versionDirectory) { throw "Runtime package not found: $package" }
 $legal=Join-Path 'artifacts/windows/agent/legal' $package
 New-Item -ItemType Directory $legal -Force | Out-Null
 $notices=Get-ChildItem $versionDirectory.FullName -File | Where-Object { $_.Name -match '^LICENSE\.TXT$|^THIRD-PARTY-NOTICES\.TXT$' }
 if (!$notices) { throw "Runtime license notices not found: $package" }
 $notices | Copy-Item -Destination $legal
}
@"
#define ProductName "$($brand.productName)"
#define CompanyName "$($brand.companyName)"
#define SupportUrl "$($brand.supportUrl)"
#define InstallerName "$($brand.installerName)"
#define InstallerImage "$($brand.installerImage)"
#define ServiceName "$($brand.windowsServiceName)"
#define ProductVersion "$version"
#define BrandBackground "$($brand.palette.sidebar)"
"@ | Set-Content deploy/windows/Branding.iss -Encoding UTF8
if (!(Test-Path $Compiler)) { throw 'Install Inno Setup 6 or supply -Compiler' }
& $Compiler deploy/windows/Setup.iss
if ($LASTEXITCODE) { throw 'Installer compilation failed' }
$file="artifacts/installer/$($brand.installerName).exe"
$info=[Diagnostics.FileVersionInfo]::GetVersionInfo((Resolve-Path $file))
$numericVersion="{0}.{1}.{2}.{3}" -f $info.FileMajorPart,$info.FileMinorPart,$info.FileBuildPart,$info.FilePrivatePart
if ($numericVersion -ne "$version.0") { throw "Installer PE version $numericVersion differs from compiled agent version $version.0" }
(Get-FileHash $file -Algorithm SHA256).Hash.ToLowerInvariant() | Set-Content "$file.sha256" -Encoding ASCII

"$version.0" | Set-Content artifacts/installer/agent-version.txt -Encoding ASCII

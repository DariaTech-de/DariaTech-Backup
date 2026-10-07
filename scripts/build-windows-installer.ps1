param([string]$Compiler="${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe")
$ErrorActionPreference='Stop'
Set-Location (Join-Path $PSScriptRoot '..')
$brand=Get-Content branding/product.json -Raw | ConvertFrom-Json
foreach ($value in $brand.PSObject.Properties.Value) { if ($value -is [string] -and ($value.Contains('"') -or $value.Contains("`n"))) { throw 'Unsafe branding value' } }
$version=([xml](Get-Content DariaTech/Agent/DariaTech.Agent.csproj)).Project.PropertyGroup.Version
dotnet publish DariaTech/Agent/DariaTech.Agent.csproj -c Release -r win-x64 --self-contained true -o artifacts/windows/agent
if ($LASTEXITCODE) { throw 'Agent publish failed' }
dotnet publish Executables/Duplicati.Server/Duplicati.Server.csproj -c Release -r win-x64 --self-contained true -p:DariaTechOssOnly=true -o artifacts/windows/engine
if ($LASTEXITCODE) { throw 'OSS engine publish failed' }
@"
#define ProductName "$($brand.productName)"
#define CompanyName "$($brand.companyName)"
#define SupportUrl "$($brand.supportUrl)"
#define InstallerName "$($brand.installerName)"
#define ServiceName "$($brand.windowsServiceName)"
#define ProductVersion "$version"
"@ | Set-Content deploy/windows/Branding.iss -Encoding UTF8
if (!(Test-Path $Compiler)) { throw 'Install Inno Setup 6 or supply -Compiler' }
& $Compiler deploy/windows/Setup.iss
if ($LASTEXITCODE) { throw 'Installer compilation failed' }
$file="artifacts/installer/$($brand.installerName).exe"
(Get-FileHash $file -Algorithm SHA256).Hash.ToLowerInvariant() | Set-Content "$file.sha256" -Encoding ASCII

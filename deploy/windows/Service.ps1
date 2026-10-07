param(
 [ValidateSet('Prepare','Install','Remove')][string]$Action,
 [Parameter(Mandatory=$true)][string]$InstallDirectory,
 [string]$ConsoleUrl='https://backup.dariatech.de'
)
$ErrorActionPreference='Stop'
$brand=Get-Content (Join-Path $InstallDirectory 'product.json') -Raw | ConvertFrom-Json
$name=$brand.windowsServiceName
$state=Join-Path $env:ProgramData 'DariaTechBackup'
try {
if ($Action -eq 'Remove') {
 $service=Get-Service $name -ErrorAction SilentlyContinue
 if ($service) { Stop-Service $name -Force; & sc.exe delete $name | Out-Null; if ($LASTEXITCODE) { throw 'Service removal failed' } }
 # Keep backup databases and DPAPI identity. Reinstallation must use the same machine/SYSTEM identity.
 Remove-Item (Join-Path $state 'enrollment-token.txt'),(Join-Path $state 'engine-password.txt') -Force -ErrorAction SilentlyContinue
 exit 0
}
if ($Action -eq 'Prepare') {
 $uri=[Uri]$ConsoleUrl
 if ($uri.Scheme -ne 'https' -or $uri.AbsolutePath -ne '/' -or $uri.UserInfo -or $uri.Query -or $uri.Fragment) { throw 'Console URL must be an HTTPS origin' }
 $service=Get-Service $name -ErrorAction SilentlyContinue
 if ($service) { Stop-Service $name -Force }
 $existingConfig=Join-Path $InstallDirectory 'appsettings.json'
 if ((Test-Path (Join-Path $state 'identity.bin')) -and (Test-Path $existingConfig)) {
  $old=Get-Content $existingConfig -Raw | ConvertFrom-Json
  if ($old.Agent.ConsoleUrl.TrimEnd('/') -ne $uri.AbsoluteUri.TrimEnd('/')) { throw 'An enrolled agent cannot change Console origin during upgrade. Re-enrollment requires explicit device revocation and local state reset.' }
 }
 New-Item -ItemType Directory -Path $state -Force | Out-Null
 $acl=New-Object System.Security.AccessControl.DirectorySecurity
 $acl.SetAccessRuleProtection($true,$false)
 foreach ($sid in 'S-1-5-18','S-1-5-32-544') {
  $identity=New-Object System.Security.Principal.SecurityIdentifier($sid)
  $rule=New-Object System.Security.AccessControl.FileSystemAccessRule($identity,'FullControl','ContainerInherit,ObjectInherit','None','Allow')
  $acl.AddAccessRule($rule)
 }
 Set-Acl -Path $state -AclObject $acl
 # Fail closed rather than claiming the bundled engine is running on a port owned by another process.
 if (Get-NetTCPConnection -LocalPort 8210 -State Listen -ErrorAction SilentlyContinue) { throw 'Port 8210 is in use; stop the conflicting application before installation' }
 @{Agent=@{ConsoleUrl=$uri.AbsoluteUri.TrimEnd('/');EngineUrl='http://127.0.0.1:8210';StateDirectory=$state;HeartbeatSeconds=60;ManageEngine=$true};Logging=@{LogLevel=@{Default='Information'}}} |
  ConvertTo-Json -Depth 5 | Set-Content (Join-Path $InstallDirectory 'appsettings.json') -Encoding UTF8
 exit 0
}
$exe=Join-Path $InstallDirectory 'DariaTech.Agent.exe'
if (!(Test-Path $exe)) { throw 'Agent executable missing' }
$service=Get-Service $name -ErrorAction SilentlyContinue
if (!$service) {
 New-Service -Name $name -DisplayName ($brand.productName+' Agent') -BinaryPathName ('"'+$exe+'"') -StartupType Automatic | Out-Null
} else {
 & sc.exe config $name binPath= ('"'+$exe+'"') start= auto obj= LocalSystem | Out-Null
 if ($LASTEXITCODE) { throw 'Service configuration failed' }
}
& sc.exe failure $name reset= 86400 actions= restart/60000/restart/60000/restart/60000 | Out-Null
if ($LASTEXITCODE) { throw 'Service recovery configuration failed' }
Start-Service $name
$deadline=(Get-Date).AddSeconds(90)
do {
 Start-Sleep -Seconds 2
 $ready=(Test-Path (Join-Path $state 'identity.bin')) -and !(Test-Path (Join-Path $state 'enrollment-token.txt'))
 $engine=Get-NetTCPConnection -LocalPort 8210 -State Listen -ErrorAction SilentlyContinue
 $service=Get-Service $name
 if ($ready -and $engine -and $service.Status -eq 'Running') { exit 0 }
} while ((Get-Date) -lt $deadline)
Stop-Service $name -Force -ErrorAction SilentlyContinue
throw 'Enrollment or engine startup failed. Check HTTPS reachability, token validity and Windows Application event log. Local state is preserved for retry.'
} catch {
 if(Test-Path $state){[IO.File]::WriteAllText((Join-Path $state 'installer-diagnostic.txt'),$_.ToString())}
 throw
}

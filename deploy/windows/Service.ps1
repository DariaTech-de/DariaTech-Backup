param(
 [ValidateSet('Prepare','Install','Remove')][string]$Action,
 [Parameter(Mandatory=$true)][string]$InstallDirectory,
 [string]$ConsoleUrl='https://backup.dariatech.de',
 # Local administrator opt-in for Console-managed backup jobs; it only ever enables, an existing opt-in is kept.
 [switch]$AllowManagedConfiguration,
 # Local administrator opt-in for Console-signed remote actions: pins the Console's public command key and
 # creates the protected restore folder that remote restores write into.
 [switch]$AllowRemoteCommands,
 [string]$CommandKeyFile
)
$ErrorActionPreference='Stop'
# The installer launches Windows PowerShell 5.1. Do not inherit PowerShell 7
# module paths from an RMM/CI parent: their Security module cannot load on .NET Framework.
$env:PSModulePath=Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\Modules'
$brand=Get-Content (Join-Path $InstallDirectory 'product.json') -Raw | ConvertFrom-Json
$name=$brand.windowsServiceName
$state=Join-Path $env:ProgramData 'DariaTechBackup'
function Test-TrustedState {
 if(!(Test-Path $state)){return $false}
 if((Get-Item $state).Attributes -band [IO.FileAttributes]::ReparsePoint){return $false}
 $existing=Get-Acl $state
 $owner=$existing.GetOwner([Security.Principal.SecurityIdentifier]).Value
 $trustedOwners=@('S-1-5-18','S-1-5-32-544',[Security.Principal.WindowsIdentity]::GetCurrent().User.Value)
 if($owner -notin $trustedOwners -or !$existing.AreAccessRulesProtected){return $false}
 foreach($rule in $existing.Access) {
  if($rule.IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value -notin $trustedOwners){return $false}
 }
 return $true
}
try {
if ($Action -eq 'Remove') {
 $service=Get-Service $name -ErrorAction SilentlyContinue
 if ($service) { Stop-Service $name -Force; & sc.exe delete $name | Out-Null; if ($LASTEXITCODE) { throw 'Service removal failed' } }
 # Keep backup databases and DPAPI identity. Reinstallation must use the same machine/SYSTEM identity.
 if(Test-TrustedState){Remove-Item (Join-Path $state 'enrollment-token.txt'),(Join-Path $state 'engine-password.txt') -Force -ErrorAction SilentlyContinue}
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
 if(Test-Path $state) {
  if(!(Test-TrustedState)){throw 'Existing agent state directory is not trusted. Verify its provenance and secure its owner/ACLs before retrying; junctions and symbolic links are refused'}
 } else { New-Item -ItemType Directory -Path $state | Out-Null }
 $acl=New-Object System.Security.AccessControl.DirectorySecurity
 $acl.SetAccessRuleProtection($true,$false)
 # Stable privileged ownership permits SYSTEM-driven upgrades without trusting an individual installer account.
 $acl.SetOwner((New-Object System.Security.Principal.SecurityIdentifier('S-1-5-32-544')))
 foreach ($sid in 'S-1-5-18','S-1-5-32-544') {
  $identity=New-Object System.Security.Principal.SecurityIdentifier($sid)
  $rule=New-Object System.Security.AccessControl.FileSystemAccessRule($identity,'FullControl','ContainerInherit,ObjectInherit','None','Allow')
  $acl.AddAccessRule($rule)
 }
 Set-Acl -Path $state -AclObject $acl
 # Fail closed rather than claiming the bundled engine is running on a port owned by another process.
 if (Get-NetTCPConnection -LocalPort 8210 -State Listen -ErrorAction SilentlyContinue) { throw 'Port 8210 is in use; stop the conflicting application before installation' }
 $agentConfiguration=@{ConsoleUrl=$uri.AbsoluteUri.TrimEnd('/');EngineUrl='http://127.0.0.1:8210';StateDirectory=$state;HeartbeatSeconds=60;ManageEngine=$true}
 if ($old -and $old.Agent) {
  foreach($property in 'AllowSaasWorkloads','AllowSaasRestore','AllowedSaasTenants','AllowUnlicensedSaasDevelopment','ExternalEngineExecutable','AllowManagedConfiguration','AllowRemoteCommands','CommandPublicKeyFile','RestoreRoot','AllowAgentUpdates','UpdatePublicKeyFile','UpdateDownloadHosts') {
   if ($old.Agent.PSObject.Properties.Name -contains $property) { $agentConfiguration[$property]=$old.Agent.$property }
  }
 }
 if ($AllowManagedConfiguration) { $agentConfiguration.AllowManagedConfiguration=$true }
 if ($AllowRemoteCommands) {
  if (!$CommandKeyFile -or !(Test-Path -LiteralPath $CommandKeyFile -PathType Leaf)) { throw 'Command key file required for remote actions' }
  $pem=(Get-Content -LiteralPath $CommandKeyFile -Raw).Trim()
  if ($pem -notmatch '^-----BEGIN PUBLIC KEY-----\r?\n[A-Za-z0-9+/=\r\n]{40,1000}\r?\n-----END PUBLIC KEY-----$') { throw 'Invalid command public key' }
  # A pinned key only changes through deliberate local administration, never through a reinstall:
  # an existing pin (also a manually provisioned path) must hold exactly the supplied key.
  $pinned=if ($agentConfiguration.CommandPublicKeyFile) { [string]$agentConfiguration.CommandPublicKeyFile } else { Join-Path $state 'commands-public.pem' }
  if (Test-Path -LiteralPath $pinned) {
   if ((Get-Content -LiteralPath $pinned -Raw).Trim() -ne $pem) { throw 'A different Console command key is already pinned on this device' }
  } elseif ($agentConfiguration.CommandPublicKeyFile) { throw 'The configured command key file is missing; restore it through local administration' }
  else { [IO.File]::WriteAllText($pinned,$pem+"`n") }
  $restoreRoot=Join-Path $state 'Restores'
  if (!(Test-Path $restoreRoot)) { New-Item -ItemType Directory -Path $restoreRoot | Out-Null }
  $restoreAcl=New-Object System.Security.AccessControl.DirectorySecurity
  $restoreAcl.SetAccessRuleProtection($true,$false)
  $restoreAcl.SetOwner((New-Object System.Security.Principal.SecurityIdentifier('S-1-5-32-544')))
  foreach ($sid in 'S-1-5-18','S-1-5-32-544') {
   $restoreAcl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule((New-Object System.Security.Principal.SecurityIdentifier($sid)),'FullControl','ContainerInherit,ObjectInherit','None','Allow')))
  }
  Set-Acl -Path $restoreRoot -AclObject $restoreAcl
  $agentConfiguration.AllowRemoteCommands=$true;$agentConfiguration.CommandPublicKeyFile=$pinned
  if (!$agentConfiguration.RestoreRoot) { $agentConfiguration.RestoreRoot=$restoreRoot }
 }
 @{Agent=$agentConfiguration;Logging=@{LogLevel=@{Default='Information'}}} |
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
 # Never write privileged diagnostics into a refused, potentially attacker-owned state path.
 [IO.File]::WriteAllText((Join-Path $InstallDirectory 'installer-diagnostic.txt'),$_.ToString())
 throw
}

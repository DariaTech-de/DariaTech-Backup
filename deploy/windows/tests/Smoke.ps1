# Run only on an ephemeral elevated Windows CI runner. No production identities or data.
$ErrorActionPreference='Stop'
if($env:GITHUB_ACTIONS -ne 'true'){throw 'This destructive fixture runs only on an ephemeral GitHub Actions runner'}
Set-Location (Join-Path $PSScriptRoot '../../..')
$temporary=Join-Path $env:RUNNER_TEMP ('dariatech-installer-'+[Guid]::NewGuid())
New-Item -ItemType Directory $temporary | Out-Null
$password=[Guid]::NewGuid().ToString('N')+[Guid]::NewGuid().ToString('N')
$token=([Guid]::NewGuid().ToString('N')+[Guid]::NewGuid().ToString('N')).ToUpperInvariant()
$cert=New-SelfSignedCertificate -DnsName localhost -CertStoreLocation Cert:\LocalMachine\My -NotAfter (Get-Date).AddDays(1)
$pfx=Join-Path $temporary 'fixture.pfx'
Export-PfxCertificate -Cert $cert -FilePath $pfx -Password (ConvertTo-SecureString $password -AsPlainText -Force) | Out-Null
$public=Join-Path $temporary 'fixture.cer'
Export-Certificate -Cert $cert -FilePath $public | Out-Null
Import-Certificate -FilePath $public -CertStoreLocation Cert:\LocalMachine\Root | Out-Null
$env:FIXTURE_PFX=$pfx;$env:FIXTURE_PASSWORD=$password;$env:FIXTURE_TOKEN=$token
$env:FIXTURE_ENGINE_PASSWORD=$password;$env:FIXTURE_RESULT=Join-Path $temporary 'result.json'
$fixture=Start-Process node -ArgumentList (Join-Path $PSScriptRoot 'console-fixture.cjs') -PassThru -NoNewWindow
function Invoke-Setup([string[]]$Arguments,[bool]$ExpectFailure=$false) {
 $log=Join-Path $temporary 'setup.log'
 $setup=Start-Process artifacts/installer/DariaTechBackupSetup.exe -ArgumentList ($Arguments+"/LOG=$log") -PassThru
 if(!$setup.WaitForExit(240000)) {
  & taskkill.exe /PID $setup.Id /T /F | Out-Null
  if(Test-Path $log){Write-Host ((Get-Content $log -Raw).Replace($password,'[REDACTED]').Replace($token,'[REDACTED]'))}
  throw 'Installer exceeded four-minute timeout'
 }
 $setup.Refresh()
 if($ExpectFailure){if($setup.ExitCode -eq 0){throw 'Installer accepted an insecure pre-existing state directory'};return}
 if($setup.ExitCode -ne 0) {
  if(Test-Path $log){Write-Host ((Get-Content $log -Raw).Replace($password,'[REDACTED]').Replace($token,'[REDACTED]'))}
  $diagnostic=Join-Path ${env:ProgramFiles} 'DariaTech Backup/installer-diagnostic.txt'
  if(Test-Path $diagnostic){Write-Host ((Get-Content $diagnostic -Raw).Replace($password,'[REDACTED]').Replace($token,'[REDACTED]'))}
  Get-WinEvent -FilterHashtable @{LogName='Application';StartTime=(Get-Date).AddMinutes(-10)} -ErrorAction SilentlyContinue |
   Where-Object {$_.ProviderName -match 'DariaTech|\.NET Runtime'} | Select-Object -First 5 |
   ForEach-Object {Write-Host ($_.Message.Replace($password,'[REDACTED]').Replace($token,'[REDACTED]'))}
  throw "Installer exited with code $($setup.ExitCode)"
 }
}
try {
 $tokenFile=Join-Path $temporary 'token.txt';$passwordFile=Join-Path $temporary 'engine-password.txt'
 [IO.File]::WriteAllText($tokenFile,$token);[IO.File]::WriteAllText($passwordFile,$password)
 for($i=0;$i -lt 30;$i++) {
  try { Invoke-RestMethod https://localhost:18443/health -TimeoutSec 5 | Out-Null;break } catch { Start-Sleep 1 }
 }
 $state=Join-Path $env:ProgramData 'DariaTechBackup'
 if(Test-Path $state){throw 'Windows smoke test requires a clean runner state directory'}
 New-Item -ItemType Directory $state | Out-Null
 Write-Host 'Verifying rejection of an insecure pre-existing state directory.'
 Invoke-Setup -Arguments @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/console=https://localhost:18443',"/tokenfile=$tokenFile","/enginepasswordfile=$passwordFile") -ExpectFailure $true
 if(Test-Path (Join-Path $state 'identity.bin')){throw 'Insecure directory was enrolled'}
 Remove-Item $state -Recurse -Force
 Write-Host 'Starting silent installation.'
 Invoke-Setup @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/console=https://localhost:18443',"/tokenfile=$tokenFile","/enginepasswordfile=$passwordFile")
 Write-Host 'Installation returned success; verifying runtime.'
 $service=Get-CimInstance Win32_Service -Filter "Name='DariaTechBackupAgent'"
 if($service.State -ne 'Running' -or $service.StartMode -ne 'Auto' -or $service.StartName -ne 'LocalSystem'){throw 'Service configuration invalid'}
 $state=Join-Path $env:ProgramData 'DariaTechBackup'
 if(Test-Path (Join-Path $state 'enrollment-token.txt')){throw 'Enrollment input was not consumed'}
 if(Test-Path (Join-Path $state 'engine-password.txt')){throw 'Engine password input was not consumed'}
 if((Get-Acl $state).GetOwner([Security.Principal.SecurityIdentifier]).Value -ne 'S-1-5-32-544'){throw 'Agent state owner is not the stable Administrators group'}
 $acl=Get-Acl $state
 if(!$acl.AreAccessRulesProtected){throw 'State directory inherits unsafe ACLs'}
 foreach($rule in $acl.Access){$sid=$rule.IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value;if($sid -notin @('S-1-5-18','S-1-5-32-544')){throw 'Unexpected state ACL principal'}}
 foreach($file in @('identity.bin','engine-credential.bin','engine-key.bin')) {
  $bytes=[IO.File]::ReadAllBytes((Join-Path $state $file))
  if([Text.Encoding]::UTF8.GetString($bytes).Contains($password)){throw 'Plaintext secret in state'}
  try { [Security.Cryptography.ProtectedData]::Unprotect($bytes,[Text.Encoding]::UTF8.GetBytes($file),[Security.Cryptography.DataProtectionScope]::CurrentUser) | Out-Null;throw 'DPAPI unexpectedly decrypts under runner identity' }
  catch [Security.Cryptography.CryptographicException] { }
 }
 $auth=Invoke-RestMethod http://127.0.0.1:8210/api/v1/auth/login -Method Post -ContentType application/json -Body (@{Password=$password;RememberMe=$false}|ConvertTo-Json)
 $headers=@{Authorization='Bearer '+$auth.AccessToken}
 $jobs=Invoke-RestMethod http://127.0.0.1:8210/api/v1/backups -Headers $headers
 if($jobs.Count -ne 0){throw 'Installer created unsolicited backup jobs'}
 $engine=$null
 for($attempt=0;$attempt -lt 40;$attempt++) {
  $owned=Get-NetTCPConnection -LocalPort 8210 -State Listen -ErrorAction SilentlyContinue|Select-Object -First 1
  if($owned){$engine=Get-CimInstance Win32_Process -Filter ("ProcessId="+$owned.OwningProcess)}
  if($engine -and $engine.Name -eq 'Duplicati.Server.exe' -and $engine.CommandLine){break}
  Start-Sleep -Milliseconds 250
 }
 if(!$engine -or !$engine.CommandLine -or $engine.Name -ne 'Duplicati.Server.exe' -or $engine.CommandLine.Contains($password)){throw 'Listening engine metadata unavailable or password exposed on command line'}
 $agentExe=Join-Path (Join-Path ${env:ProgramFiles} 'DariaTech Backup') 'DariaTech.Agent.exe'
 $engineProcess=Get-Process -Id $engine.ProcessId;$ticks=$engineProcess.StartTime.ToUniversalTime().Ticks
 & $agentExe --check-engine-port $engine.ProcessId $ticks 8210
 if($LASTEXITCODE){throw 'Owned engine TCP connection was rejected'}
 $rejected=Start-Process $agentExe -ArgumentList @('--check-engine-port',$PID.ToString(),((Get-Process -Id $PID).StartTime.ToUniversalTime().Ticks).ToString(),'8210') -Wait -PassThru -NoNewWindow -RedirectStandardError (Join-Path $temporary 'ownership-rejected.txt')
 if(!$rejected.ExitCode){throw 'Foreign process ownership was accepted for the engine port'}
 Write-Host 'PASS: authenticated connection ownership; rejected a foreign owner before sending HTTP data.'
 $restoreProbe=Join-Path $state 'restore-security-probe';New-Item -ItemType Directory $restoreProbe|Out-Null
 $rootAcl=New-Object Security.AccessControl.DirectorySecurity;$rootAcl.SetAccessRuleProtection($true,$false);$rootAcl.SetOwner((New-Object Security.Principal.SecurityIdentifier('S-1-5-32-544')))
 foreach($sid in 'S-1-5-18','S-1-5-32-544'){$rootAcl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule((New-Object Security.Principal.SecurityIdentifier($sid)),'FullControl','ContainerInherit,ObjectInherit','None','Allow')))}
 Set-Acl $restoreProbe $rootAcl
 & $agentExe --check-restore-root $restoreProbe
 if($LASTEXITCODE){throw 'Protected restore root was rejected'}
 $rootAcl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule((New-Object Security.Principal.SecurityIdentifier('S-1-1-0')),'Read','Allow')));Set-Acl $restoreProbe $rootAcl
 $rejected=Start-Process $agentExe -ArgumentList @('--check-restore-root',('"'+$restoreProbe+'"')) -Wait -PassThru -NoNewWindow -RedirectStandardError (Join-Path $temporary 'restore-root-rejected.txt')
 if(!$rejected.ExitCode){throw 'World-readable restore root was accepted'}
 Remove-Item $restoreProbe -Force
 Write-Host 'PASS: protected restore root accepted; root exposed to ordinary users rejected.'


 for($i=0;$i -lt 80;$i++) {
  if(Test-Path $env:FIXTURE_RESULT){$result=Get-Content $env:FIXTURE_RESULT -Raw|ConvertFrom-Json;if($result.enrolled -and $result.heartbeats -gt 0){break}}
  Start-Sleep 1
 }
 if(!$result -or !$result.enrolled -or $result.heartbeats -lt 1){throw 'No authenticated engine-reachable heartbeat received'}
 $identityHash=(Get-FileHash (Join-Path $state 'identity.bin')).Hash
 $credentialHash=(Get-FileHash (Join-Path $state 'engine-credential.bin')).Hash
 $configFile=Join-Path (Join-Path ${env:ProgramFiles} 'DariaTech Backup') 'appsettings.json'
 $configuration=Get-Content $configFile -Raw|ConvertFrom-Json
 if($configuration.Agent.AllowManagedConfiguration){throw 'Console-managed jobs must stay disabled without the local /allowmanaged opt-in'}
 # Exercise selection of a separately installed engine using the OSS fixture.
 # This validates the BYOL boundary; it does not claim a licensed SaaS backup.
 $externalRoot=Join-Path ${env:ProgramFiles} ('DariaTech External Engine Fixture-'+[Guid]::NewGuid())
 New-Item -ItemType Directory $externalRoot | Out-Null
 $engineAcl=New-Object Security.AccessControl.DirectorySecurity
 $engineAcl.SetAccessRuleProtection($true,$false)
 $engineAcl.SetOwner((New-Object Security.Principal.SecurityIdentifier('S-1-5-32-544')))
 foreach($sid in 'S-1-5-18','S-1-5-32-544'){$engineAcl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule((New-Object Security.Principal.SecurityIdentifier($sid)),'FullControl','ContainerInherit,ObjectInherit','None','Allow')))}
 Set-Acl $externalRoot $engineAcl
 Copy-Item (Join-Path (Split-Path $agentExe) 'engine/*') $externalRoot -Recurse
 $externalExe=Join-Path $externalRoot 'Duplicati.Server.exe'
 & $agentExe --check-engine-installation $externalExe
 if($LASTEXITCODE){throw 'Protected external engine was rejected'}
 $engineAcl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule((New-Object Security.Principal.SecurityIdentifier('S-1-1-0')),'Write','ContainerInherit,ObjectInherit','None','Allow')))
 Set-Acl $externalRoot $engineAcl
 $rejected=Start-Process $agentExe -ArgumentList @('--check-engine-installation',('"'+$externalExe+'"')) -Wait -PassThru -NoNewWindow -RedirectStandardError (Join-Path $temporary 'external-engine-rejected.txt')
 if(!$rejected.ExitCode){throw 'User-writable external engine was accepted'}
 $engineAcl.RemoveAccessRuleAll((New-Object Security.AccessControl.FileSystemAccessRule((New-Object Security.Principal.SecurityIdentifier('S-1-1-0')),'Write','ContainerInherit,ObjectInherit','None','Allow')))
 Set-Acl $externalRoot $engineAcl
 $configuration.Agent | Add-Member ExternalEngineExecutable $externalExe -Force
 $configuration.Agent | Add-Member CommandPublicKeyFile (Join-Path $state 'commands-public.pem') -Force
 $configuration.Agent | Add-Member RestoreRoot (Join-Path $state 'Restores') -Force
 $configuration | ConvertTo-Json -Depth 5 | Set-Content $configFile -Encoding UTF8
 # The upgrade opts in through the installer parameter; trust settings are preserved from the existing configuration.
 Invoke-Setup @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/console=https://localhost:18443','/allowmanaged=1')
 $preserved=Get-Content $configFile -Raw|ConvertFrom-Json
 if($preserved.Agent.ExternalEngineExecutable -ne $externalExe){throw 'Upgrade discarded external engine selection'}
 $selected=Get-CimInstance Win32_Process -Filter "Name='Duplicati.Server.exe'"
 if($selected.ExecutablePath -ne $externalExe){throw 'Agent did not launch the selected external engine'}
 if(!$preserved.Agent.AllowManagedConfiguration -or $preserved.Agent.CommandPublicKeyFile -ne $configuration.Agent.CommandPublicKeyFile -or $preserved.Agent.RestoreRoot -ne $configuration.Agent.RestoreRoot){throw 'Upgrade discarded local management opt-in or trust settings'}
 if((Get-FileHash (Join-Path $state 'identity.bin')).Hash -ne $identityHash -or (Get-FileHash (Join-Path $state 'engine-credential.bin')).Hash -ne $credentialHash){throw 'Upgrade changed enrolled identity or engine credential'}
 Stop-Service DariaTechBackupAgent
 Start-Sleep 3
 if(Get-Process Duplicati.Server -ErrorAction SilentlyContinue){throw 'Engine survived service stop'}
 Start-Service DariaTechBackupAgent
 Start-Sleep 10
 if(!(Get-NetTCPConnection -LocalPort 8210 -State Listen -ErrorAction SilentlyContinue)){throw 'Engine did not recover after service restart'}
 $uninstall=Join-Path ${env:ProgramFiles} 'DariaTech Backup/unins000.exe'
 $removed=Start-Process $uninstall -ArgumentList '/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART' -Wait -PassThru
 if($removed.ExitCode -ne 0){throw 'Uninstall failed'}
 if(Get-Service DariaTechBackupAgent -ErrorAction SilentlyContinue){throw 'Service survived uninstall'}
 if(!(Test-Path (Join-Path $state 'identity.bin'))){throw 'Uninstall erased retained identity'}
 Write-Host 'PASS: insecure-state rejection, silent setup, SYSTEM DPAPI, ACLs, enrollment, real engine authentication, heartbeat, upgrade, restart and uninstall.'
} finally {
 Stop-Service DariaTechBackupAgent -Force -ErrorAction SilentlyContinue
 Stop-Process -Id $fixture.Id -Force -ErrorAction SilentlyContinue
 Remove-Item "Cert:\LocalMachine\Root\$($cert.Thumbprint)","Cert:\LocalMachine\My\$($cert.Thumbprint)" -ErrorAction SilentlyContinue
 if($externalRoot -and (Test-Path $externalRoot)){Remove-Item $externalRoot -Recurse -Force -ErrorAction SilentlyContinue}
 Remove-Item $temporary -Recurse -Force -ErrorAction SilentlyContinue
}

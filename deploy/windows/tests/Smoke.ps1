# Run only on an ephemeral elevated Windows CI runner. No production identities or data.
$ErrorActionPreference='Stop'
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
function Invoke-Setup([string[]]$Arguments) {
 $log=Join-Path $temporary 'setup.log'
 $setup=Start-Process artifacts/installer/DariaTechBackupSetup.exe -ArgumentList ($Arguments+"/LOG=$log") -PassThru
 if(!$setup.WaitForExit(240000)) {
  & taskkill.exe /PID $setup.Id /T /F | Out-Null
  if(Test-Path $log){Write-Host ((Get-Content $log -Raw).Replace($password,'[REDACTED]').Replace($token,'[REDACTED]'))}
  throw 'Installer exceeded four-minute timeout'
 }
 $setup.Refresh()
 if($setup.ExitCode -ne 0) {
  if(Test-Path $log){Write-Host ((Get-Content $log -Raw).Replace($password,'[REDACTED]').Replace($token,'[REDACTED]'))}
  $diagnostic=Join-Path $env:ProgramData 'DariaTechBackup/installer-diagnostic.txt'
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
 Write-Host 'Starting silent installation.'
 Invoke-Setup @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/console=https://localhost:18443',"/tokenfile=$tokenFile","/enginepasswordfile=$passwordFile")
 Write-Host 'Installation returned success; verifying runtime.'
 $service=Get-CimInstance Win32_Service -Filter "Name='DariaTechBackupAgent'"
 if($service.State -ne 'Running' -or $service.StartMode -ne 'Auto' -or $service.StartName -ne 'LocalSystem'){throw 'Service configuration invalid'}
 $state=Join-Path $env:ProgramData 'DariaTechBackup'
 if(Test-Path (Join-Path $state 'enrollment-token.txt')){throw 'Enrollment input was not consumed'}
 if(Test-Path (Join-Path $state 'engine-password.txt')){throw 'Engine password input was not consumed'}
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
 $engine=Get-CimInstance Win32_Process -Filter "Name='Duplicati.Server.exe'"
 if(!$engine -or $engine.CommandLine.Contains($password)){throw 'Engine missing or password exposed on command line'}
 for($i=0;$i -lt 80;$i++) {
  if(Test-Path $env:FIXTURE_RESULT){$result=Get-Content $env:FIXTURE_RESULT -Raw|ConvertFrom-Json;if($result.enrolled -and $result.heartbeats -gt 0){break}}
  Start-Sleep 1
 }
 if(!$result -or !$result.enrolled -or $result.heartbeats -lt 1){throw 'No authenticated engine-reachable heartbeat received'}
 $identityHash=(Get-FileHash (Join-Path $state 'identity.bin')).Hash
 $credentialHash=(Get-FileHash (Join-Path $state 'engine-credential.bin')).Hash
 Invoke-Setup @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/console=https://localhost:18443')
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
 Write-Host 'PASS: silent setup, SYSTEM DPAPI, ACLs, enrollment, real engine authentication, heartbeat, upgrade, restart and uninstall.'
} finally {
 Stop-Service DariaTechBackupAgent -Force -ErrorAction SilentlyContinue
 Stop-Process -Id $fixture.Id -Force -ErrorAction SilentlyContinue
 Remove-Item "Cert:\LocalMachine\Root\$($cert.Thumbprint)","Cert:\LocalMachine\My\$($cert.Thumbprint)" -ErrorAction SilentlyContinue
 Remove-Item $temporary -Recurse -Force -ErrorAction SilentlyContinue
}

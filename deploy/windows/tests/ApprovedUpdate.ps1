# Destructive only inside an ephemeral elevated GitHub runner. All keys, accounts and artifacts are test fixtures.
$ErrorActionPreference='Stop'
if($env:GITHUB_ACTIONS -ne 'true'){throw 'Approved-update integration requires an ephemeral GitHub Actions runner'}
Set-Location (Join-Path $PSScriptRoot '../../..')
$temp=Join-Path $env:RUNNER_TEMP ('dariatech-update-'+[Guid]::NewGuid());New-Item -ItemType Directory $temp|Out-Null
$newInstaller=Join-Path $temp 'new.exe';Copy-Item artifacts/installer/DariaTechBackupSetup.exe $newInstaller
$newChecksum=Get-Content artifacts/installer/DariaTechBackupSetup.exe.sha256
$newVersion=(Get-Content artifacts/installer/agent-version.txt).Trim()
$state=Join-Path $env:ProgramData 'DariaTechBackup';$fixture=$null;$cert=$null
try {
 if(Get-Service DariaTechBackupAgent -ErrorAction SilentlyContinue){throw 'Prior installer smoke test must finish uninstalling before update fixture'}
 if(Test-Path $state){Remove-Item $state -Recurse -Force}
 # Same tested source, two genuinely different compiled agent/installer versions.
 $stale=Join-Path 'artifacts/windows/engine' 'Duplicati.Proprietary.Sentinel.dll'
 [IO.File]::WriteAllText((Join-Path (Get-Location) $stale),'CI-only stale payload sentinel, not executable code')
 ./scripts/build-windows-installer.ps1 -VersionOverride '0.1.0'
 if(Test-Path $stale){throw 'Stale restricted payload survived OSS packaging'}
 $oldInstaller=Join-Path $temp 'old.exe';Copy-Item artifacts/installer/DariaTechBackupSetup.exe $oldInstaller
 $password=[Guid]::NewGuid().ToString('N')+[Guid]::NewGuid().ToString('N')
 $token=([Guid]::NewGuid().ToString('N')+[Guid]::NewGuid().ToString('N')).ToUpperInvariant()
 $cert=New-SelfSignedCertificate -DnsName localhost -CertStoreLocation Cert:\LocalMachine\My -NotAfter (Get-Date).AddDays(1)
 $pfx=Join-Path $temp 'tls.pfx';Export-PfxCertificate -Cert $cert -FilePath $pfx -Password (ConvertTo-SecureString $password -AsPlainText -Force)|Out-Null
 $cer=Join-Path $temp 'tls.cer';Export-Certificate -Cert $cert -FilePath $cer|Out-Null;Import-Certificate -FilePath $cer -CertStoreLocation Cert:\LocalMachine\Root|Out-Null
 $env:UPDATE_PFX=$pfx;$env:UPDATE_PASSWORD=$password;$env:UPDATE_TOKEN=$token;$env:UPDATE_INSTALLER=$newInstaller
 $env:UPDATE_STAGE=Join-Path $temp 'stage.json';$env:UPDATE_RESULT=Join-Path $temp 'result.json'
 @{phase=0}|ConvertTo-Json|Set-Content $env:UPDATE_STAGE -Encoding UTF8
 $fixture=Start-Process node -ArgumentList (Join-Path $PSScriptRoot 'update-fixture.cjs') -PassThru -NoNewWindow
 for($i=0;$i -lt 30;$i++){try{Invoke-RestMethod https://localhost:18444/health -TimeoutSec 5|Out-Null;break}catch{Start-Sleep 1}}
 $tokenFile=Join-Path $temp 'token.txt';$passwordFile=Join-Path $temp 'password.txt';[IO.File]::WriteAllText($tokenFile,$token);[IO.File]::WriteAllText($passwordFile,$password)
 $setup=Start-Process $oldInstaller -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/console=https://localhost:18444',"/tokenfile=$tokenFile","/enginepasswordfile=$passwordFile") -PassThru -Wait
 if($setup.ExitCode -ne 0){throw 'Old-version fixture installer failed'}
 Stop-Service DariaTechBackupAgent
 $identityHash=(Get-FileHash (Join-Path $state 'identity.bin')).Hash
 $key=[Security.Cryptography.ECDsa]::Create([Security.Cryptography.ECCurve+NamedCurves]::nistP256)
 $publicKey=Join-Path $state 'updates-public.pem';[IO.File]::WriteAllText($publicKey,$key.ExportSubjectPublicKeyInfoPem())
 $configFile=Join-Path (Join-Path ${env:ProgramFiles} 'DariaTech Backup') 'appsettings.json';$config=Get-Content $configFile -Raw|ConvertFrom-Json
 $config.Agent.HeartbeatSeconds=10
 $config.Agent|Add-Member -NotePropertyName AllowAgentUpdates -NotePropertyValue $true -Force
 $config.Agent|Add-Member -NotePropertyName UpdatePublicKeyFile -NotePropertyValue $publicKey -Force
 $config.Agent|Add-Member -NotePropertyName UpdateDownloadHosts -NotePropertyValue @('localhost') -Force
 $config|ConvertTo-Json -Depth 5|Set-Content $configFile -Encoding UTF8
 Start-Service DariaTechBackupAgent
 function Set-Phase([int]$Phase,[bool]$BadSignature,[bool]$Tamper) {
  $manifest=@{ReleaseId=[Guid]::NewGuid().ToString();Sequence=$Phase;Product='DariaTechBackupAgent';Platform='win-x64';Version=$newVersion;ArtifactUrl='https://localhost/artifact.exe';Sha256=(Get-FileHash $newInstaller -Algorithm SHA256).Hash;Length=(Get-Item $newInstaller).Length;Issued=[DateTimeOffset]::UtcNow.ToString('O');Expires=[DateTimeOffset]::UtcNow.AddHours(1).ToString('O')}
  $bytes=[Text.Encoding]::UTF8.GetBytes(($manifest|ConvertTo-Json -Compress))
  $signature=$key.SignData($bytes,[Security.Cryptography.HashAlgorithmName]::SHA256)
  if($BadSignature){$signature[0]=$signature[0] -bxor 1}
  @{phase=$Phase;tamper=$Tamper;assignment=@{deploymentId=[Guid]::NewGuid().ToString();manifest=@{payload=[Convert]::ToBase64String($bytes);signature=[Convert]::ToBase64String($signature)}}}|ConvertTo-Json -Depth 6 -Compress|Set-Content $env:UPDATE_STAGE -Encoding UTF8
 }
 function Wait-Receipt([int]$Phase,[string]$Status) {
  for($i=0;$i -lt 240;$i++){
   if(Test-Path $env:UPDATE_RESULT){$r=Get-Content $env:UPDATE_RESULT -Raw|ConvertFrom-Json;if($r.receipts|Where-Object {$_.phase -eq $Phase -and $_.status -eq $Status}){return}}
   Start-Sleep 1
  }
  if(Test-Path $env:UPDATE_RESULT){$observed=Get-Content $env:UPDATE_RESULT -Raw|ConvertFrom-Json;Write-Host ('Observed update receipts: '+($observed.receipts|ConvertTo-Json -Compress));Write-Host ('Observed compiled versions: '+($observed.versions|Select-Object -Unique|ConvertTo-Json -Compress))}
  $service=Get-Service DariaTechBackupAgent -ErrorAction SilentlyContinue;Write-Host ('Service state: '+$service.Status)
  $installedExe=Join-Path (Split-Path $configFile) 'DariaTech.Agent.exe';if(Test-Path $installedExe){Write-Host ('Installed file version: '+[Diagnostics.FileVersionInfo]::GetVersionInfo($installedExe).FileVersion)}
  $diagnostic=Join-Path (Split-Path $configFile) 'installer-diagnostic.txt';if(Test-Path $diagnostic){Write-Host ('Installer diagnostic: '+(Get-Content $diagnostic -Raw))}
  Get-WinEvent -FilterHashtable @{LogName='Application';StartTime=(Get-Date).AddMinutes(-5)} -ErrorAction SilentlyContinue|Where-Object {$_.ProviderName -match 'DariaTech|\.NET Runtime|Application Error'}|Select-Object -First 6|ForEach-Object {Write-Host ('Agent event: '+$_.Message)}
  throw "Missing update receipt: phase $Phase, status $Status"
 }
 Set-Phase 1 $true $false;Wait-Receipt 1 'Rejected'
 if((Get-Service DariaTechBackupAgent).Status -ne 'Running'){throw 'Invalid signature stopped the agent'}
 Set-Phase 2 $false $true;Wait-Receipt 2 'Failed'
 if((Get-Service DariaTechBackupAgent).Status -ne 'Running'){throw 'Tampered artifact stopped the agent'}
 Set-Phase 3 $false $false;Wait-Receipt 3 'Installed'
 if((Get-FileHash (Join-Path $state 'identity.bin')).Hash -ne $identityHash){throw 'Approved update replaced enrollment identity'}
 $exe=Join-Path (Split-Path $configFile) 'DariaTech.Agent.exe'
 if([Diagnostics.FileVersionInfo]::GetVersionInfo($exe).FileVersion -ne $newVersion){throw 'Installed agent version differs from approved manifest'}
 $preserved=Get-Content $configFile -Raw|ConvertFrom-Json
 if(!$preserved.Agent.AllowAgentUpdates -or $preserved.Agent.UpdatePublicKeyFile -ne $publicKey){throw 'Approved update lost local trust/opt-in'}
 if((Get-Service DariaTechBackupAgent).Status -ne 'Running'){throw 'Agent did not restart after approved update'}
 if(!(Get-NetTCPConnection -LocalPort 8210 -State Listen -ErrorAction SilentlyContinue)){throw 'Bundled engine did not restart after approved update'}
 Write-Host 'PASS: rejected bad signature and tampered binary; signed two-version remote update; identity/trust retained; actual service and engine restarted.'
 # Fault injection for operator recovery: service offline and a missing installed binary.
 # Use exactly the already approved artifact; never synthesize an unsigned replacement or downgrade.
 $approvedHash=(Get-FileHash $newInstaller -Algorithm SHA256).Hash
 Stop-Service DariaTechBackupAgent;Remove-Item $exe -Force
 if((Get-Service DariaTechBackupAgent).Status -ne 'Stopped' -or (Test-Path $exe)){throw 'Recovery fault injection failed'}
 if((Get-FileHash $newInstaller -Algorithm SHA256).Hash -ne $approvedHash){throw 'Recovery artifact changed'}
 $repair=Start-Process $newInstaller -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/console=https://localhost:18444') -PassThru -Wait
 if($repair.ExitCode -ne 0){throw 'Verified installer recovery failed'}
 if((Get-Service DariaTechBackupAgent).Status -ne 'Running' -or [Diagnostics.FileVersionInfo]::GetVersionInfo($exe).FileVersion -ne $newVersion){throw 'Recovery did not restore the approved running agent'}
 if((Get-FileHash (Join-Path $state 'identity.bin')).Hash -ne $identityHash){throw 'Recovery changed enrollment identity'}
 $restored=Get-Content $configFile -Raw|ConvertFrom-Json
 if(!$restored.Agent.AllowAgentUpdates -or $restored.Agent.UpdatePublicKeyFile -ne $publicKey){throw 'Recovery lost pinned update trust'}
 Write-Host 'PASS: offline/missing-binary recovery from the verified approved installer; enrollment and pinned trust retained.'

 $key.Dispose()
} finally {
 if(Get-Service DariaTechBackupAgent -ErrorAction SilentlyContinue){Stop-Service DariaTechBackupAgent -Force; & sc.exe delete DariaTechBackupAgent | Out-Null}
 if($fixture -and !$fixture.HasExited){Stop-Process $fixture.Id -Force}
 if($cert){Remove-Item "Cert:\LocalMachine\Root\$($cert.Thumbprint)" -ErrorAction SilentlyContinue;Remove-Item "Cert:\LocalMachine\My\$($cert.Thumbprint)" -ErrorAction SilentlyContinue}
 Copy-Item $newInstaller artifacts/installer/DariaTechBackupSetup.exe -Force
 $newChecksum|Set-Content artifacts/installer/DariaTechBackupSetup.exe.sha256 -Encoding ASCII
 $newVersion|Set-Content artifacts/installer/agent-version.txt -Encoding ASCII
}

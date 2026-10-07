#include "Branding.iss"
[Setup]
AppId={{53F55FCA-80E2-4314-BE49-2103CCCFB211}
AppName={#ProductName}
AppVersion={#ProductVersion}
AppPublisher={#CompanyName}
AppSupportURL={#SupportUrl}
DefaultDirName={autopf}\{#ProductName}
DefaultGroupName={#ProductName}
OutputDir=..\..\artifacts\installer
OutputBaseFilename={#InstallerName}
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
MinVersion=10.0.17763
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
LicenseFile=..\..\LICENSE
CloseApplications=yes
RestartApplications=no
UninstallDisplayName={#ProductName}
SetupLogging=no

[Files]
Source: "..\..\artifacts\windows\agent\*"; DestDir: "{app}"; Excludes: "appsettings.json"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\..\artifacts\windows\agent\appsettings.json"; DestDir: "{app}"; Flags: onlyifdoesntexist
Source: "..\..\artifacts\windows\engine\*"; DestDir: "{app}\engine"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "Service.ps1"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\..\LICENSE"; DestDir: "{app}\legal"; Flags: ignoreversion
Source: "..\..\thirdparty\*"; DestDir: "{app}\legal\thirdparty"; Excludes: "*.dll,*.exe,*.zip,*.nupkg"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\..\DariaTech\Console\wwwroot\legal\management\*"; DestDir: "{app}\legal\management"; Flags: ignoreversion

[UninstallRun]
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File ""{app}\Service.ps1"" -Action Remove -InstallDirectory ""{app}"""; Flags: runhidden waituntilterminated; RunOnceId: "RemoveService"

[Code]
var ConnectionPage: TInputQueryWizardPage;
    Token, TokenFile, ConsoleUrl, EnginePassword, EnginePasswordFile: String;
    SetupError: String;

function GetCustomSetupExitCode: Integer;
begin
 if SetupError <> '' then Result := 10 else Result := 0;
end;

procedure CurPageChanged(CurPageID: Integer);
begin
 if (CurPageID = wpFinished) and (SetupError <> '') then begin
  WizardForm.FinishedHeadingLabel.Caption := 'Installation failed';
  WizardForm.FinishedLabel.Caption := SetupError + #13#10 + 'The local state is retained for diagnosis. Correct the error and run setup again.';
 end;
end;

procedure InitializeWizard;
begin
 ConsoleUrl := ExpandConstant('{param:console|https://backup.dariatech.de}');
 Token := ExpandConstant('{param:token|}');
 TokenFile := ExpandConstant('{param:tokenfile|}');
 EnginePasswordFile := ExpandConstant('{param:enginepasswordfile|}');
 ConnectionPage := CreateInputQueryPage(wpSelectDir, 'Console registration',
  'Register this device with DariaTech', 'Enter the HTTPS Console URL and a single-use enrollment token. The local engine password protects the UI on this PC.');
 ConnectionPage.Add('Console URL:', False);
 ConnectionPage.Add('Enrollment token:', True);
 ConnectionPage.Add('Local engine password (14–200 characters; optional for silent installation):', True);
 ConnectionPage.Values[0] := ConsoleUrl;
 ConnectionPage.Values[1] := Token;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
 Result := True;
 if CurPageID = ConnectionPage.ID then begin
  if (Length(ConnectionPage.Values[1]) <> 64) and (TokenFile = '') and
     not FileExists(ExpandConstant('{commonappdata}\DariaTechBackup\identity.bin')) then begin
   MsgBox('Enter the 64-character enrollment token.', mbError, MB_OK); Result := False;
  end;
  if not FileExists(ExpandConstant('{commonappdata}\DariaTechBackup\engine-credential.bin')) and
     ((Length(ConnectionPage.Values[2]) < 14) or (Length(ConnectionPage.Values[2]) > 200)) then begin
   MsgBox('Enter a local engine password with 14–200 characters.', mbError, MB_OK); Result := False;
  end;
 end;
end;

procedure RunHelper(Action: String);
var Code: Integer;
begin
 if not Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
  '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' + ExpandConstant('{app}\Service.ps1') +
  '" -Action ' + Action + ' -InstallDirectory "' + ExpandConstant('{app}') + '" -ConsoleUrl "' + ConsoleUrl + '"',
  '', SW_HIDE, ewWaitUntilTerminated, Code) then RaiseException('Could not start service installer.');
 if Code <> 0 then RaiseException('Service installation failed. Check the Windows Application event log, enrollment token and HTTPS reachability. Local state is preserved.');
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var Code: Integer;
begin
 Result := '';
  if not Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
   '-NoProfile -NonInteractive -Command "$ErrorActionPreference=''Stop''; $s=Get-Service ''{#ServiceName}'' -ErrorAction SilentlyContinue; if($s){Stop-Service $s.Name -Force; $s.WaitForStatus(''Stopped'',[TimeSpan]::FromSeconds(30))}; exit 0"',
   '', SW_HIDE, ewWaitUntilTerminated, Code) then begin
   Result := 'Could not start service preflight.'; Exit;
  end;
  if Code <> 0 then Result := 'Could not stop previous service.';
end;

procedure CurStepChanged(CurStep: TSetupStep);
var State: String; ReadToken: AnsiString;
begin
 if CurStep <> ssPostInstall then Exit;
 try
 ConsoleUrl := ConnectionPage.Values[0]; Token := ConnectionPage.Values[1]; EnginePassword := ConnectionPage.Values[2];
 if (Pos('"', ConsoleUrl) > 0) or (Pos(#13, ConsoleUrl) > 0) or (Pos(#10, ConsoleUrl) > 0) then RaiseException('Invalid Console URL.');
 State := ExpandConstant('{commonappdata}\DariaTechBackup');
 if not FileExists(State+'\identity.bin') then begin
  if TokenFile <> '' then begin
   if not LoadStringFromFile(TokenFile, ReadToken) then RaiseException('Cannot read token file.');
   Token := Trim(String(ReadToken));
  end;
  if Length(Token) <> 64 then RaiseException('A 64-character enrollment token or /tokenfile is required.');
 end;
 RunHelper('Prepare');
 if not FileExists(State+'\identity.bin') then
  if not SaveStringToFile(State+'\enrollment-token.txt', AnsiString(Token), False) then RaiseException('Cannot save enrollment input.');
 if EnginePasswordFile <> '' then begin
  if not LoadStringFromFile(EnginePasswordFile, ReadToken) then RaiseException('Cannot read local password file.');
  EnginePassword := Trim(UTF8Decode(ReadToken));
 end;
 if (EnginePassword <> '') and ((Length(EnginePassword) < 14) or (Length(EnginePassword) > 200)) then RaiseException('Local password must be 14–200 characters.');
 if (EnginePassword <> '') and not FileExists(State+'\engine-credential.bin') then
  if not SaveStringToFile(State+'\engine-password.txt', UTF8Encode(EnginePassword), False) then RaiseException('Cannot save local password.');
 RunHelper('Install');
 Token := ''; EnginePassword := ''; ConnectionPage.Values[1] := ''; ConnectionPage.Values[2] := '';
 except
  SetupError := GetExceptionMessage;
  Log('Service provisioning failed: ' + SetupError);
 end;
end;

#include "Branding.iss"
[Setup]
AppId={{53F55FCA-80E2-4314-BE49-2103CCCFB211}
AppName={#ProductName}
AppVersion={#ProductVersion}
VersionInfoVersion={#ProductVersion}.0
VersionInfoProductVersion={#ProductVersion}.0
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
WizardStyle=modern dark windows11 includetitlebar hidebevels
WizardSizePercent=120
WizardBackColor={#BrandBackground}
WizardImageFile=..\..\branding\installer-wizard-100.png,..\..\branding\installer-wizard-150.png,..\..\branding\installer-wizard-200.png
WizardImageBackColor={#BrandBackground}
WizardImageStretch=no
WizardSmallImageBackColor={#BrandBackground}
DisableWelcomePage=no
DisableProgramGroupPage=yes
DisableDirPage=yes
LanguageDetectionMethod=none
ShowLanguageDialog=no
WizardSmallImageFile=..\..\branding\installer-small-100.png,..\..\branding\installer-small-150.png,..\..\branding\installer-small-200.png
LicenseFile=..\..\LICENSE
CloseApplications=yes
RestartApplications=no
UninstallDisplayName={#ProductName}
SetupLogging=no

[Languages]
Name: "german"; MessagesFile: "compiler:Languages\German.isl"

[Messages]
SetupAppTitle={#ProductName} – Setup
SetupWindowTitle={#ProductName} – Setup
WelcomeLabel1=Willkommen bei {#ProductName}
WelcomeLabel2=Zuverlässige Datensicherung – betreut von {#CompanyName}.%n%n•  Läuft als Windows-Dienst und sichert auch ohne angemeldeten Benutzer.%n•  Verschlüsselt Ihre Daten, bevor sie diesen PC verlassen.%n•  Meldet den Sicherungsstatus an Ihre DariaTech Console.%n%nHalten Sie die Console-Adresse und den Registrierungstoken bereit. Die Einrichtung dauert etwa eine Minute.
WizardLicense=Lizenzhinweise
LicenseLabel=Die Backup-Engine basiert auf Open-Source-Software (Duplicati, MIT-Lizenz).
LicenseLabel3=Bitte lesen Sie die folgenden Lizenzhinweise.
WizardReady=Bereit zur Installation
ReadyLabel1=Alles ist vorbereitet.
ReadyLabel2a=Klicken Sie auf „Installieren“, um {#ProductName} einzurichten und dieses Gerät bei Ihrer Console zu registrieren.
WizardInstalling=Installation läuft
InstallingLabel=Bitte warten Sie, während {#ProductName} eingerichtet wird. Sie können dieses Fenster minimieren.
FinishedHeadingLabel={#ProductName} ist bereit
FinishedLabel=Der Agent ist installiert und mit Ihrer Console verbunden. Der Dienst startet künftig automatisch mit Windows.%n%nNächster Schritt: Richten Sie Ihre Backup-Jobs in der lokalen Oberfläche ein (http://127.0.0.1:8210/ngax).
ClickFinish=Klicken Sie auf „Fertigstellen“, um den Assistenten zu schließen.
BeveledLabel={#CompanyName} · {#SupportUrl}

[Files]
Source: "..\..\artifacts\windows\agent\*"; DestDir: "{app}"; Excludes: "appsettings.json"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\..\artifacts\windows\agent\appsettings.json"; DestDir: "{app}"; Flags: onlyifdoesntexist
Source: "..\..\artifacts\windows\engine\*"; DestDir: "{app}\engine"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "Service.ps1"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\..\LICENSE"; DestDir: "{app}\legal"; Flags: ignoreversion
Source: "..\..\thirdparty\*"; DestDir: "{app}\legal\thirdparty"; Excludes: "*.dll,*.exe,*.zip,*.nupkg"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\..\DariaTech\Console\wwwroot\legal\management\*"; DestDir: "{app}\legal\management"; Flags: ignoreversion

[Run]
Filename: "http://127.0.0.1:8210/ngax"; Description: "Lokale Backup-Oberfläche jetzt öffnen"; Flags: postinstall shellexec nowait skipifsilent runasoriginaluser; Check: InstallSucceeded

[UninstallRun]
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File ""{app}\Service.ps1"" -Action Remove -InstallDirectory ""{app}"""; Flags: runhidden waituntilterminated; RunOnceId: "RemoveService"

[Code]
var ConnectionPage: TInputQueryWizardPage;
    Token, TokenFile, ConsoleUrl, EnginePassword, EnginePasswordFile: String;
    SetupError: String;

function InstallSucceeded: Boolean;
begin
 Result := SetupError = '';
end;

function GetCustomSetupExitCode: Integer;
begin
 if SetupError <> '' then Result := 10 else Result := 0;
end;

procedure CurPageChanged(CurPageID: Integer);
begin
 if (CurPageID = wpFinished) and (SetupError <> '') then begin
  WizardForm.FinishedHeadingLabel.Caption := 'Installation fehlgeschlagen';
  WizardForm.FinishedLabel.Caption := SetupError + #13#10 + 'Lokale Daten bleiben erhalten. Beheben Sie den Fehler und starten Sie die Installation erneut.';
 end;
end;

procedure InitializeWizard;
begin
 ConsoleUrl := ExpandConstant('{param:console|https://backup.dariatech.de}');
 Token := ExpandConstant('{param:token|}');
 TokenFile := ExpandConstant('{param:tokenfile|}');
 EnginePasswordFile := ExpandConstant('{param:enginepasswordfile|}');
 ConnectionPage := CreateInputQueryPage(wpSelectDir, 'Gerät registrieren',
  'Mit Ihrer DariaTech Console verbinden',
  'Der Registrierungstoken gilt einmalig und ordnet dieses Gerät dem richtigen Kunden und Standort zu. ' +
  'Sie erzeugen ihn in der Console unter Kunden › Gerät hinzufügen.' + #13#10#13#10 +
  'Das lokale Passwort schützt die Backup-Oberfläche auf diesem PC.');
 ConnectionPage.Add('Console-Adresse (HTTPS):', False);
 ConnectionPage.Add('Registrierungstoken (64 Zeichen):', True);
 ConnectionPage.Add('Passwort für die lokale Oberfläche (14–200 Zeichen):', True);
 ConnectionPage.Values[0] := ConsoleUrl;
 ConnectionPage.Values[1] := Token;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
 Result := True;
 // Inno calls page validation during silent navigation too. File inputs are
 // validated below; never open an interactive message box in unattended setup.
 if WizardSilent then Exit;
 if CurPageID = ConnectionPage.ID then begin
  if (Length(ConnectionPage.Values[1]) <> 64) and (TokenFile = '') and
     not FileExists(ExpandConstant('{commonappdata}\DariaTechBackup\identity.bin')) then begin
   MsgBox('Geben Sie den Registrierungstoken mit 64 Zeichen ein.', mbError, MB_OK); Result := False;
  end;
  if (EnginePasswordFile = '') and not FileExists(ExpandConstant('{commonappdata}\DariaTechBackup\engine-credential.bin')) and
     ((Length(ConnectionPage.Values[2]) < 14) or (Length(ConnectionPage.Values[2]) > 200)) then begin
   MsgBox('Geben Sie ein lokales Passwort mit 14–200 Zeichen ein.', mbError, MB_OK); Result := False;
  end;
 end;
end;

procedure ShowStatus(Text: String);
begin
 WizardForm.StatusLabel.Caption := Text;
 WizardForm.FilenameLabel.Caption := '';
end;

procedure RunHelper(Action: String);
var Code: Integer;
begin
 if Action = 'Prepare' then ShowStatus('Geschütztes Datenverzeichnis wird vorbereitet …')
 else ShowStatus('Gerät wird bei der Console registriert und der Dienst gestartet …');
 if not Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
  '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' + ExpandConstant('{app}\Service.ps1') +
  '" -Action ' + Action + ' -InstallDirectory "' + ExpandConstant('{app}') + '" -ConsoleUrl "' + ConsoleUrl + '"',
  '', SW_HIDE, ewWaitUntilTerminated, Code) then RaiseException('Die Dienstinstallation konnte nicht gestartet werden.');
 if Code <> 0 then RaiseException('Dienstinstallation fehlgeschlagen. Prüfen Sie Registrierungstoken, HTTPS-Verbindung und Windows-Anwendungsprotokoll. Lokale Daten bleiben erhalten.');
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var Code: Integer;
begin
 Result := '';
  if not Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
   '-NoProfile -NonInteractive -Command "$ErrorActionPreference=''Stop''; $env:PSModulePath=$env:SystemRoot+''\System32\WindowsPowerShell\v1.0\Modules''; $s=Get-Service ''{#ServiceName}'' -ErrorAction SilentlyContinue; if($s){Stop-Service $s.Name -Force; $s.WaitForStatus(''Stopped'',[TimeSpan]::FromSeconds(30))}; exit 0"',
   '', SW_HIDE, ewWaitUntilTerminated, Code) then begin
   Result := 'Die Dienstprüfung konnte nicht gestartet werden.'; Exit;
  end;
  if Code <> 0 then Result := 'Der vorhandene Dienst konnte nicht angehalten werden.';
end;

procedure CurStepChanged(CurStep: TSetupStep);
var State: String; ReadToken: AnsiString;
begin
 if CurStep <> ssPostInstall then Exit;
 try
 ConsoleUrl := ConnectionPage.Values[0]; Token := ConnectionPage.Values[1]; EnginePassword := ConnectionPage.Values[2];
 if (Pos('"', ConsoleUrl) > 0) or (Pos(#13, ConsoleUrl) > 0) or (Pos(#10, ConsoleUrl) > 0) then RaiseException('Ungültige Console-Adresse.');
 State := ExpandConstant('{commonappdata}\DariaTechBackup');
 if not FileExists(State+'\identity.bin') then begin
  if TokenFile <> '' then begin
   if not LoadStringFromFile(TokenFile, ReadToken) then RaiseException('Tokendatei kann nicht gelesen werden.');
   Token := Trim(String(ReadToken));
  end;
  if Length(Token) <> 64 then RaiseException('Ein Registrierungstoken mit 64 Zeichen oder /tokenfile ist erforderlich.');
 end;
 RunHelper('Prepare');
 if not FileExists(State+'\identity.bin') then
  if not SaveStringToFile(State+'\enrollment-token.txt', AnsiString(Token), False) then RaiseException('Registrierungstoken kann nicht gespeichert werden.');
 if EnginePasswordFile <> '' then begin
  if not LoadStringFromFile(EnginePasswordFile, ReadToken) then RaiseException('Lokale Passwortdatei kann nicht gelesen werden.');
  EnginePassword := Trim(UTF8Decode(ReadToken));
 end;
 if (EnginePassword <> '') and ((Length(EnginePassword) < 14) or (Length(EnginePassword) > 200)) then RaiseException('Das lokale Passwort muss 14–200 Zeichen lang sein.');
 if (EnginePassword <> '') and not FileExists(State+'\engine-credential.bin') then
  if not SaveStringToFile(State+'\engine-password.txt', UTF8Encode(EnginePassword), False) then RaiseException('Lokales Passwort kann nicht gespeichert werden.');
 RunHelper('Install');
 Token := ''; EnginePassword := ''; ConnectionPage.Values[1] := ''; ConnectionPage.Values[2] := '';
 except
  SetupError := GetExceptionMessage;
  Log('Service provisioning failed: ' + SetupError);
 end;
end;

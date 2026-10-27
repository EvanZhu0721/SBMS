#define AppName "SBMS"
#ifndef AppVersion
#define AppVersion "0.0.0-dev"
#endif
#ifndef OutputSuffix
#define OutputSuffix ""
#endif
#define AppPublisher "SBMS"
#define AppExeName "sbms-tray.exe"

[Setup]
AppId={{7EB4D7A8-16A9-4B6F-82E3-31A77BC81B6A}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={autopf64}\SBMS
DefaultGroupName=SBMS
DisableProgramGroupPage=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
PrivilegesRequired=admin
OutputDir=..\target\installer
OutputBaseFilename=SBMS-Setup-{#AppVersion}-x64{#OutputSuffix}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern dynamic windows11 hidebevels
WizardBackColor=#f8f3f9
WizardBackColorDynamicDark=#2a282d
WizardSizePercent=125,125
DisableWelcomePage=yes
UninstallDisplayIcon={app}\{#AppExeName}
CloseApplications=no
RestartApplications=no
ChangesEnvironment=no
SetupLogging=yes
#ifndef UnsignedBuild
SignTool=sbmssign
SignedUninstaller=yes
#endif

[Files]
Source: "assets\intro-*.png"; Flags: dontcopy
Source: "..\README.md"; DestName: "sbms-readme.txt"; Flags: dontcopy
Source: "..\target\release\sbms.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\target\release\sbms-tray.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\target\driver\SBMSIndirectDisplay.inf"; DestDir: "{app}\driver"; Flags: ignoreversion
Source: "..\target\driver\SBMSIndirectDisplay.dll"; DestDir: "{app}\driver"; Flags: ignoreversion
Source: "..\target\driver\SBMSIndirectDisplay.cat"; DestDir: "{app}\driver"; Flags: ignoreversion
Source: "maintenance.ps1"; DestDir: "{app}\installer"; Flags: ignoreversion
Source: "driver-trust.ps1"; DestDir: "{app}\installer"; Flags: ignoreversion
#ifdef LocalCertificateThumbprint
Source: "..\target\driver\SBMS.cer"; DestDir: "{app}\driver"; Flags: ignoreversion
#endif
Source: "manage-sunshine-instance.ps1"; DestDir: "{app}\installer"; Flags: ignoreversion
Source: "..\NOTICE.md"; DestDir: "{app}\licenses"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}\licenses"; DestName: "GPL-3.0.txt"; Flags: ignoreversion
Source: "..\LICENSES\MS-PL.txt"; DestDir: "{app}\licenses"; Flags: ignoreversion
Source: "..\LICENSES\Apache-2.0.txt"; DestDir: "{app}\licenses"; Flags: ignoreversion
; Run maintenance after the final installed file, before Inno evaluates NeedRestart.
Source: "..\LICENSES\LGPL-2.1.txt"; DestDir: "{app}\licenses"; Flags: ignoreversion; AfterInstall: InstallSbms
Source: "maintenance.ps1"; DestName: "sbms-maintenance.ps1"; Flags: dontcopy

[InstallDelete]
; User state under {localappdata}\SBMS is intentionally preserved during upgrades.
Type: files; Name: "{app}\diagnose-sbms.ps1"
Type: files; Name: "{app}\install-sbms-driver.ps1"
Type: files; Name: "{app}\install-sbms-program-files.ps1"
Type: files; Name: "{app}\README.md"
Type: files; Name: "{app}\RELEASE_NOTES.md"
Type: files; Name: "{app}\run-sbms-native.ps1"
Type: files; Name: "{app}\SBMSDeviceHost.exe"
Type: files; Name: "{app}\SBMSNative.exe"
Type: files; Name: "{app}\SBMSSetup.exe"
Type: files; Name: "{app}\driver\IddSampleDriver.cer"
Type: filesandordirs; Name: "{app}\driver\IddSampleDriver"

[UninstallDelete]
; A deliberate uninstall removes configuration, display overrides, and diagnostics.
Type: filesandordirs; Name: "{localappdata}\SBMS"
Type: files; Name: "{app}\diagnose-sbms.ps1"
Type: files; Name: "{app}\install-sbms-driver.ps1"
Type: files; Name: "{app}\install-sbms-program-files.ps1"
Type: files; Name: "{app}\README.md"
Type: files; Name: "{app}\RELEASE_NOTES.md"
Type: files; Name: "{app}\run-sbms-native.ps1"
Type: files; Name: "{app}\SBMSDeviceHost.exe"
Type: files; Name: "{app}\SBMSNative.exe"
Type: files; Name: "{app}\SBMSSetup.exe"
Type: files; Name: "{app}\driver\IddSampleDriver.cer"
Type: filesandordirs; Name: "{app}\driver\IddSampleDriver"

[Icons]
Name: "{group}\SBMS"; Filename: "{app}\sbms-tray.exe"; Parameters: "--open"; WorkingDir: "{app}"
Name: "{group}\Uninstall SBMS"; Filename: "{uninstallexe}"

[Code]
var
  MaintenanceLogPath: String;
  TrustCertificate: Boolean;
  DriverRestartRequired: Boolean;
  MaintenanceFailed: Boolean;

#include "installer-ui.iss"

procedure InitializeWizard();
begin
  InitializeInstallerUI();
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  InstallerUIPageChanged(CurPageID);
end;

procedure CurInstallProgressChanged(CurProgress, MaxProgress: Integer);
begin
  UpdateInstallerProgress(CurProgress, MaxProgress);
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := InstallerUINextButtonClick(CurPageID);
end;

procedure DeinitializeSetup();
begin
  DeinitializeInstallerUI();
end;

procedure LogMaintenanceOutput(const S: String; const Error, FirstLine: Boolean);
begin
  Log(S);
  SaveStringToFile(MaintenanceLogPath, UTF8Encode(S + #13#10), True);
end;

function MaintenanceFailure(Message: String): String;
begin
  Result := Message + #13#10#13#10 + 'Details: ' + MaintenanceLogPath;
end;

function PowerShellPath(): String;
begin
  Result := ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe');
end;

function InitializeMaintenanceLog(Path, Header: String): Boolean;
begin
  if FileExists(Path) and not FileCopy(Path, Path + '.previous', False) then
    Log('Could not preserve previous maintenance log: ' + Path);
  Result := SaveStringToFile(Path, UTF8Encode(Header), False);
end;

function RunMaintenanceScript(
  ScriptPath: String;
  Action: String;
  Root: String;
  var ResultCode: Integer): Boolean;
var
  Parameters: String;
  DetailLogPath, Header: String;
begin
  MaintenanceLogPath :=
    ExpandConstant('{commonappdata}\SBMS\logs\setup-') + Action + '.log';
  Header := GetDateTimeString('yyyy-mm-dd hh:nn:ss', '-', ':') +
    ' ' + Action + #13#10;
  if not ForceDirectories(ExtractFileDir(MaintenanceLogPath)) or
    not InitializeMaintenanceLog(MaintenanceLogPath, Header) then
  begin
    ResultCode := 5;
    Result := False;
    exit;
  end;
  if not FileExists(ScriptPath) then
  begin
    LogMaintenanceOutput('Maintenance script is missing: ' + ScriptPath, True, True);
    ResultCode := 2;
    Result := False;
    exit;
  end;
  Parameters :=
    '-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' + ScriptPath +
    '" -Action "' + Action + '" -InstallRoot "' + Root + '"';
  DetailLogPath := MaintenanceLogPath + '.details.log';
  if InitializeMaintenanceLog(DetailLogPath, Header) then
    Parameters := Parameters + ' -DiagnosticLogPath "' + DetailLogPath + '"'
  else
    LogMaintenanceOutput('Could not initialize detailed log: ' + DetailLogPath, True, False);
  if (Action = 'Install') and TrustCertificate then
    Parameters := Parameters + ' -TrustCertificate';
  Result := ExecAndLogOutput(
    PowerShellPath(),
    Parameters,
    '',
    SW_HIDE,
    ewWaitUntilTerminated,
    ResultCode,
    @LogMaintenanceOutput);
  if not Result then
    LogMaintenanceOutput(SysErrorMessage(ResultCode), True, False);
  LogMaintenanceOutput(Format('Exit code: %d', [ResultCode]), False, False);
end;

function RunMaintenance(Action: String; Root: String; var ResultCode: Integer): Boolean;
begin
  Result := RunMaintenanceScript(
    AddBackslash(Root) + 'installer\maintenance.ps1',
    Action,
    Root,
    ResultCode);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
  ScriptPath: String;
begin
  Result := '';
#ifdef LocalCertificateThumbprint
  if not (
    RegKeyExists(HKLM64, 'SOFTWARE\Microsoft\SystemCertificates\ROOT\Certificates\{#LocalCertificateThumbprint}') and
    RegKeyExists(HKLM64, 'SOFTWARE\Microsoft\SystemCertificates\TrustedPublisher\Certificates\{#LocalCertificateThumbprint}')) then
  begin
    if WizardSilent then
    begin
      Result := 'This build uses a self-signed driver certificate. Run Setup interactively to review and approve certificate trust.';
      exit;
    end;
    if not TrustCertificate then
      TrustCertificate := MsgBox(
        'This build uses a self-signed SBMS driver certificate, not Microsoft certification.' + #13#10#13#10 +
        'To install the driver, Setup must add its signing certificate to this computer''s Trusted Root and Trusted Publishers stores. Windows will trust other code signed with this certificate too.' + #13#10#13#10 +
        'Certificate: {#LocalCertificateThumbprint}' + #13#10#13#10 +
        'No Secure Boot or driver-signature enforcement settings will be changed. The certificate remains trusted after uninstall.' + #13#10#13#10 +
        'Do you approve trusting this certificate?',
        mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES;
    if not TrustCertificate then
    begin
      Result := 'Certificate trust was not approved. No installation changes were started.';
      exit;
    end;
  end;
#endif
  if DirExists(ExpandConstant('{app}')) then
  begin
    ExtractTemporaryFile('sbms-maintenance.ps1');
    ScriptPath := ExpandConstant('{tmp}\sbms-maintenance.ps1');
    if not RunMaintenanceScript(
      ScriptPath,
      'PrepareUpgrade',
      ExpandConstant('{app}'),
      ResultCode) then
      Result := MaintenanceFailure('Could not start the SBMS upgrade preparation helper.')
    else if ResultCode <> 0 then
      Result := MaintenanceFailure('The existing SBMS session or configuration could not be preserved safely. Setup was cancelled.');
  end;
end;

procedure InstallSbms();
var
  ResultCode: Integer;
begin
  MaintenanceFailed := True;
  if not RunMaintenance('Install', ExpandConstant('{app}'), ResultCode) then
    RaiseException(MaintenanceFailure('Could not start SBMS installation maintenance.'))
  else if ResultCode = 3010 then
    DriverRestartRequired := True
  else if ResultCode <> 0 then
    RaiseException(MaintenanceFailure(Format('SBMS driver or startup registration failed (exit code %d).', [ResultCode])));
  MaintenanceFailed := False;
end;

function NeedRestart(): Boolean;
begin
  Result := DriverRestartRequired;
end;

function GetCustomSetupExitCode(): Integer;
begin
  Result := 0;
  if MaintenanceFailed then Result := 1;
end;

function InitializeUninstall(): Boolean;
var
  ResultCode: Integer;
begin
  Result := RunMaintenance('PreflightUninstall', ExpandConstant('{app}'), ResultCode);
  if not Result then
    MsgBox(MaintenanceFailure('Could not start SBMS uninstall preflight.'), mbError, MB_OK)
  else if ResultCode <> 0 then
  begin
    MsgBox(
      MaintenanceFailure(Format('SBMS uninstall preflight failed (exit code %d). No uninstall changes were started.', [ResultCode])),
      mbError,
      MB_OK);
    Result := False;
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ResultCode: Integer;
begin
  if CurUninstallStep = usUninstall then
  begin
    if not RunMaintenance('Uninstall', ExpandConstant('{app}'), ResultCode) then
      RaiseException(MaintenanceFailure('Could not start SBMS uninstall maintenance.'))
    else if ResultCode <> 0 then
      RaiseException(
        MaintenanceFailure(Format('SBMS external cleanup failed or was only partially compensated (exit code %d). Application files were retained.', [ResultCode])));
  end;
end;

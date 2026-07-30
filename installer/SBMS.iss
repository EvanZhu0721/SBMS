#define AppName "SBMS"
#ifndef AppVersion
#define AppVersion "0.0.0-dev"
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
PrivilegesRequired=admin
OutputDir=..\target\installer
OutputBaseFilename=SBMS-Setup-{#AppVersion}-x64
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\{#AppExeName}
CloseApplications=no
RestartApplications=no
ChangesEnvironment=no
SetupLogging=yes
SignTool=sbmssign
SignedUninstaller=yes

[Files]
Source: "..\target\release\sbms.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\target\release\sbms-tray.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\target\driver\SBMSIndirectDisplay.inf"; DestDir: "{app}\driver"; Flags: ignoreversion
Source: "..\target\driver\SBMSIndirectDisplay.dll"; DestDir: "{app}\driver"; Flags: ignoreversion
Source: "..\target\driver\SBMSIndirectDisplay.cat"; DestDir: "{app}\driver"; Flags: ignoreversion
Source: "maintenance.ps1"; DestDir: "{app}\installer"; Flags: ignoreversion
Source: "restart-sunshine.ps1"; DestDir: "{app}\installer"; Flags: ignoreversion
Source: "..\NOTICE.md"; DestDir: "{app}\licenses"; Flags: ignoreversion
Source: "..\LICENSES\MS-PL.txt"; DestDir: "{app}\licenses"; Flags: ignoreversion
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
Name: "{group}\SBMS"; Filename: "{app}\sbms-tray.exe"
Name: "{group}\Uninstall SBMS"; Filename: "{uninstallexe}"

[Code]
function PowerShellPath(): String;
begin
  Result := ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe');
end;

function RunMaintenanceScript(
  ScriptPath: String;
  Action: String;
  Root: String;
  var ResultCode: Integer): Boolean;
var
  Parameters: String;
begin
  if not FileExists(ScriptPath) then
  begin
    ResultCode := 2;
    Result := False;
    exit;
  end;
  Parameters :=
    '-NoLogo -NoProfile -NonInteractive -File "' + ScriptPath +
    '" -Action "' + Action + '" -InstallRoot "' + Root + '"';
  Result := Exec(
    PowerShellPath(),
    Parameters,
    '',
    SW_HIDE,
    ewWaitUntilTerminated,
    ResultCode);
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
  if DirExists(ExpandConstant('{app}')) then
  begin
    ExtractTemporaryFile('sbms-maintenance.ps1');
    ScriptPath := ExpandConstant('{tmp}\sbms-maintenance.ps1');
    if not RunMaintenanceScript(
      ScriptPath,
      'PrepareUpgrade',
      ExpandConstant('{app}'),
      ResultCode) then
      Result := 'Could not start the SBMS upgrade preparation helper.'
    else if ResultCode <> 0 then
      Result := 'The existing SBMS session or configuration could not be preserved safely. Setup was cancelled.';
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
begin
  if CurStep = ssPostInstall then
  begin
    if not RunMaintenance('Install', ExpandConstant('{app}'), ResultCode) then
      RaiseException('Could not start SBMS installation maintenance.')
    else if ResultCode <> 0 then
      RaiseException(Format('SBMS driver or startup registration failed (exit code %d).', [ResultCode]));
  end;
end;

function InitializeUninstall(): Boolean;
var
  ResultCode: Integer;
begin
  Result := RunMaintenance('PreflightUninstall', ExpandConstant('{app}'), ResultCode);
  if not Result then
    MsgBox('Could not start SBMS uninstall preflight.', mbError, MB_OK)
  else if ResultCode <> 0 then
  begin
    MsgBox(
      Format('SBMS uninstall preflight failed (exit code %d). No uninstall changes were started.', [ResultCode]),
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
      RaiseException('Could not start SBMS uninstall maintenance.')
    else if ResultCode <> 0 then
      RaiseException(
        Format('SBMS external cleanup failed or was only partially compensated (exit code %d). Application files were retained.', [ResultCode]));
  end;
end;

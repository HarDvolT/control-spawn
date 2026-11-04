; Compile with Inno Setup 6.3+. This file is never executed during packaging checks.
#ifndef PayloadDir
  #define PayloadDir "..\build\out\Prism"
#endif
#ifndef SetupOutputDir
  #define SetupOutputDir "..\build\out"
#endif
[Setup]
AppId={{C72C2E1F-2F54-4A63-BD51-93FE11BC64A2}
AppName=Control Spawn
AppVersion=1.0.3
AppPublisher=Control Spawn
SetupIconFile=..\src\Prism.App\Assets\ControlSpawn.ico
DefaultDirName={autopf}\Control Spawn
; Older versions used Program Files\Prism. Without this Inno reuses that folder; the helper removes it after the move.
UsePreviousAppDir=no
DisableDirPage=yes
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
MinVersion=10.0.22000
OutputDir={#SetupOutputDir}
OutputBaseFilename=ControlSpawnSetup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\app\ControlSpawn.exe
CloseApplications=yes
RestartApplications=no
SetupLogging=yes

[Tasks]
Name: startup; Description: "Open Control Spawn in the system tray when I sign in (the service always starts with Windows)"

[Files]
Source: "{#PayloadDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "Configure-ControlSpawn.ps1"; Flags: dontcopy

[Icons]
Name: "{commonprograms}\Control Spawn"; Filename: "{app}\app\ControlSpawn.exe"; WorkingDir: "{app}\app"

[Run]
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File ""{app}\installer\Configure-ControlSpawn.ps1"" -Mode UserStartup -InstallDir ""{app}"""; Tasks: startup; Flags: runhidden runasoriginaluser
Filename: "{app}\app\ControlSpawn.exe"; Description: "Launch Control Spawn"; WorkingDir: "{app}\app"; Flags: postinstall nowait skipifsilent runasoriginaluser; Check: CanLaunchApp

[Code]
var
  ConflictPage: TInputOptionWizardPage;
  ConflictNames: TStringList;
  RebootRequired: Boolean;
  RemoveSettings: Boolean;

// OpenRGB's release ZIP leaves out the Visual C++ runtime; games install it on most PCs, but a fresh Windows may lack it.
function HasVcRuntime: Boolean;
var Installed: Cardinal;
begin
  Result := RegQueryDWordValue(HKLM64, 'SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64', 'Installed', Installed) and (Installed = 1);
end;

function InitializeSetup: Boolean;
var Version: TWindowsVersion;
begin
  GetWindowsVersionEx(Version);
  Result := (Version.Major = 10) and (Version.Build >= 22000) and
    (Version.ProductType = VER_NT_WORKSTATION);
  if not Result then
    SuppressibleMsgBox('Control Spawn requires Windows 11 x64 (a desktop edition).', mbCriticalError, MB_OK, IDOK)
  else if not HasVcRuntime then
    SuppressibleMsgBox('The bundled OpenRGB needs the Microsoft Visual C++ Runtime (x64), which is not installed.' + #13#10#13#10 +
      'Control Spawn will install and run, but case lighting stays off until you install it from:' + #13#10 +
      'https://aka.ms/vs/17/release/vc_redist.x64.exe', mbInformation, MB_OK, IDOK);
end;

function RunHelper(const Script, Mode, Extra: String; var Code: Integer): Boolean;
begin
  Result := Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' + Script +
    '" -Mode ' + Mode + ' -InstallDir "' + ExpandConstant('{app}') + '" ' + Extra,
    '', SW_HIDE, ewWaitUntilTerminated, Code);
end;

procedure InitializeWizard;
var
  Names: TArrayOfString;
  I, Index: Integer;
  Name, DisplayName: String;
begin
  ConflictNames := TStringList.Create;
  ConflictPage := CreateInputOptionPage(wpSelectTasks, 'Hardware software conflicts',
    'Choose which detected services Control Spawn may stop and disable.',
    'Checked services will be stopped and disabled, not uninstalled. Control Spawn restores their prior startup and running state when uninstalled. Uncheck any service you want to keep enabled.', False, False);
  if RegGetSubkeyNames(HKLM64, 'SYSTEM\CurrentControlSet\Services', Names) then
    for I := 0 to GetArrayLength(Names) - 1 do begin
      Name := Names[I];
      DisplayName := '';
      RegQueryStringValue(HKLM64, 'SYSTEM\CurrentControlSet\Services\' + Name, 'DisplayName', DisplayName);
      if (CompareText(Name, 'AsusFanControlService') = 0) or
         (CompareText(Name, 'LightingService') = 0) or
         (CompareText(Name, 'ArmouryCrateService') = 0) or
         (Pos('corsair', Lowercase(Name)) = 1) or
         (Pos('icue', Lowercase(Name)) = 1) or
         (Pos('icue', Lowercase(DisplayName)) > 0) then begin
        ConflictNames.Add(Name);
        Index := ConflictPage.Add('Stop and disable ' + Name + ' (' + DisplayName + ')');
        ConflictPage.Values[Index] := True;
      end;
    end;
  WizardForm.FinishedLabel.Caption := 'Control Spawn is installed.' + #13#10#13#10 +
    'In your BIOS, disable "Download & Install ARMOURY CRATE app" to prevent ASUS software from being reinstalled.';
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := (PageID = ConflictPage.ID) and (ConflictNames.Count = 0);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var Code: Integer;
begin
  Result := '';
  ExtractTemporaryFile('Configure-ControlSpawn.ps1');
  if not RunHelper(ExpandConstant('{tmp}\Configure-ControlSpawn.ps1'), 'Prepare', '', Code) then
    Result := 'Could not run the Control Spawn preparation helper.'
  else if Code <> 0 then
    Result := 'Could not gracefully stop the existing Control Spawn service. No service was force-killed. See setup log.';
end;

procedure CurStepChanged(CurStep: TSetupStep);
var I, Code: Integer; Selected: String;
begin
  if CurStep = ssPostInstall then begin
    Selected := '';
    for I := 0 to ConflictNames.Count - 1 do
      if ConflictPage.Values[I] then begin
        if Selected <> '' then Selected := Selected + ',';
        Selected := Selected + ConflictNames[I];
      end;
    if not RunHelper(ExpandConstant('{app}\installer\Configure-ControlSpawn.ps1'), 'Install',
      '-DisableServices "' + Selected + '"', Code) then
      RaiseException('Could not run the Control Spawn configuration helper.');
    if (Code <> 0) and (Code <> 3010) then
      RaiseException('Control Spawn configuration failed. Conflict restoration was attempted. See ControlSpawn-setup-helper.log in your temp folder.');
    RebootRequired := Code = 3010;
  end;
end;

function NeedRestart: Boolean;
begin
  Result := RebootRequired;
end;

function CanLaunchApp: Boolean;
begin
  Result := not RebootRequired;
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if CurPageID = wpFinished then
    WizardForm.FinishedLabel.Caption := WizardForm.FinishedLabel.Caption + #13#10#13#10 +
      'BIOS reminder: disable "Download & Install ARMOURY CRATE app".';
end;

function InitializeUninstall: Boolean;
var
  Form: TSetupForm;
  RemoveSettingsBox: TNewCheckBox;
  ContinueButton, CancelButton: TNewButton;
begin
  Result := True;
  RemoveSettings := False;
  if UninstallSilent then exit;
  #if VER >= EncodeVer(6, 6, 0)
    Form := CreateCustomForm(480, 125, False, False);
  #else
    Form := CreateCustomForm;
    Form.ClientWidth := ScaleX(480);
    Form.ClientHeight := ScaleY(125);
  #endif
  try
    Form.Caption := 'Uninstall Control Spawn';
    Form.Position := poScreenCenter;
    RemoveSettingsBox := TNewCheckBox.Create(Form);
    RemoveSettingsBox.Parent := Form;
    RemoveSettingsBox.SetBounds(ScaleX(16), ScaleY(16), ScaleX(448), ScaleY(48));
    RemoveSettingsBox.Caption := 'Remove settings from %ProgramData%\Control Spawn (cannot be undone)';
    RemoveSettingsBox.Checked := False;
    ContinueButton := TNewButton.Create(Form);
    ContinueButton.Parent := Form;
    ContinueButton.SetBounds(ScaleX(270), ScaleY(80), ScaleX(90), ScaleY(25));
    ContinueButton.Caption := 'Continue';
    ContinueButton.ModalResult := mrOk;
    ContinueButton.Default := True;
    CancelButton := TNewButton.Create(Form);
    CancelButton.Parent := Form;
    CancelButton.SetBounds(ScaleX(370), ScaleY(80), ScaleX(90), ScaleY(25));
    CancelButton.Caption := 'Cancel';
    CancelButton.ModalResult := mrCancel;
    CancelButton.Cancel := True;
    Result := Form.ShowModal = mrOk;
    if Result then RemoveSettings := RemoveSettingsBox.Checked;
  finally
    Form.Free;
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var Code: Integer; Extra: String;
begin
  if CurUninstallStep = usUninstall then begin
    Extra := '';
    if RemoveSettings then Extra := '-RemoveSettings';
    if not RunHelper(ExpandConstant('{app}\installer\Configure-ControlSpawn.ps1'), 'Uninstall', Extra, Code) then
      RaiseException('Could not run the Control Spawn uninstall helper; files have been retained.');
    if Code <> 0 then
      RaiseException('Control Spawn services could not be removed/restored safely; files have been retained. See uninstall log.');
  end;
end;

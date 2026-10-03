; Setup.exe for the .NET Code Quality Pack (Inno Setup 6, free: https://jrsoftware.org/isinfo.php).
; Build it with Build-Installers.ps1, which passes the version and the folder holding the .vsix files.
;
; What Setup does:
;   1. Asks the user to close Visual Studio if it is running.
;   2. Copies the .vsix and the install scripts to %LOCALAPPDATA%\Programs\DotNetCodeQualityPack.
;   3. Runs Install.ps1, which installs the extension into every Visual Studio 2022 on the computer.
;   4. Adds an entry under Settings > Apps so the extension can be removed like any other program.
; No administrator rights are needed: Visual Studio extensions are installed per user.

#ifndef AppVersion
  #define AppVersion "2.0.0"
#endif
#ifndef Stage
  #define Stage "..\dist\stage"
#endif

[Setup]
AppId={{B3F1C2A4-7D2E-4E8B-9A61-5C0D3E7F9A12}
AppName=.NET Code Quality Pack for Visual Studio
AppVersion={#AppVersion}
AppVerName=.NET Code Quality Pack {#AppVersion}
AppPublisher=Rohit Joshi
DefaultDirName={localappdata}\Programs\DotNetCodeQualityPack
DisableDirPage=yes
DisableProgramGroupPage=yes
DisableReadyPage=no
PrivilegesRequired=lowest
OutputDir=..\dist
OutputBaseFilename=DotNetCodeQualityPack-Setup-{#AppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=no
SetupLogging=yes
UninstallDisplayName=.NET Code Quality Pack for Visual Studio
VersionInfoVersion={#AppVersion}
VersionInfoDescription=.NET Code Quality Pack installer for Visual Studio 2022

[Messages]
WelcomeLabel2=This installs the .NET Code Quality Pack into Visual Studio 2022 on this computer.%n%nIt adds Tools > .NET Code Quality, the Code Quality Fixer and Team Rules windows, and shared analyzers for .NET and .NET Framework solutions.%n%nClose Visual Studio before you continue.
FinishedLabel=The .NET Code Quality Pack is installed.%n%nStart Visual Studio. On the first start it installs the bundled extensions (Roslynator, SonarQube for IDE, CodeMaid and Fine Code Coverage) from the Marketplace, so keep the internet connected.%n%nThen open a solution and choose Tools > .NET Code Quality.

[Files]
Source: "{#Stage}\*.vsix"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#Stage}\Install.ps1"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#Stage}\Uninstall.ps1"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#Stage}\README.txt"; DestDir: "{app}"; Flags: ignoreversion

[UninstallRun]
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\Uninstall.ps1"" -Quiet"; Flags: runhidden waituntilterminated; RunOnceId: "RemoveFromVisualStudio"

[Code]
function IsVisualStudioRunning(): Boolean;
var
  Locator, Service, Processes: Variant;
begin
  Result := False;
  try
    Locator := CreateOleObject('WbemScripting.SWbemLocator');
    Service := Locator.ConnectServer('.', 'root\CIMV2');
    Processes := Service.ExecQuery('SELECT ProcessId FROM Win32_Process WHERE Name = ''devenv.exe''');
    Result := Processes.Count > 0;
  except
    Result := False;
  end;
end;

function WaitForVisualStudioToClose(): Boolean;
begin
  Result := True;
  while IsVisualStudioRunning() do
  begin
    if MsgBox('Visual Studio is running.' + #13#10 + #13#10 +
              'Save your work, close every Visual Studio window, then click Retry.',
              mbError, MB_RETRYCANCEL) = IDCANCEL then
    begin
      Result := False;
      Exit;
    end;
  end;
end;

function InitializeSetup(): Boolean;
begin
  Result := WaitForVisualStudioToClose();
end;

function InitializeUninstall(): Boolean;
begin
  Result := WaitForVisualStudioToClose();
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
  Msg: String;
begin
  if CurStep = ssPostInstall then
  begin
    WizardForm.StatusLabel.Caption := 'Installing into Visual Studio. This can take a minute...';
    if not Exec('powershell.exe',
                '-NoProfile -ExecutionPolicy Bypass -File "' + ExpandConstant('{app}\Install.ps1') + '" -Quiet',
                '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
      ResultCode := -1;

    Msg := '';
    case ResultCode of
      0: Msg := '';
      1: Msg := 'Visual Studio 2022 was not found on this computer. The extension needs Visual Studio 2022.';
      2: Msg := 'Visual Studio was started while Setup was running. Close it and run Setup again.';
    else
      Msg := 'Installing into Visual Studio failed (code ' + IntToStr(ResultCode) + ').' + #13#10 + #13#10 +
             'Details: ' + ExpandConstant('{%TEMP}') + '\DotNetCodeQualityPack-install.log';
    end;

    if Msg <> '' then
      MsgBox(Msg, mbError, MB_OK);
  end;
end;

; Sendspin Windows Client - Inno Setup Script
; https://jrsoftware.org/isinfo.php

#define MyAppName "WindowsSpin"
; Version can be overridden from command line: /DMyAppVersion=x.x.x
#ifndef MyAppVersion
  #define MyAppVersion "1.0.0"
#endif
; Build type: "framework" (requires .NET) or "selfcontained" (standalone)
; Override from command line: /DBuildType=selfcontained
#ifndef BuildType
  #define BuildType "framework"
#endif
#define MyAppPublisher "chrisuthe"
#define MyAppURL "https://github.com/chrisuthe/windowsSpin"
#define MyAppExeName "SendspinClient.exe"
#define MyAppAssocName "WindowsSpin Client"
#define IsSelfContained BuildType == "selfcontained"

[Setup]
; Application identity
AppId={{8E7F4A2B-5C3D-4E6F-9A1B-2C3D4E5F6A7B}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}/issues
AppUpdatesURL={#MyAppURL}/releases

; Installation directories
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes

; Output settings
OutputDir=..\dist
#if IsSelfContained
OutputBaseFilename=WindowsSpin-{#MyAppVersion}-Setup-SelfContained
#else
OutputBaseFilename=WindowsSpin-{#MyAppVersion}-Setup
#endif
SetupIconFile=..\src\SendspinClient\Resources\Icons\sendspinTray.ico
UninstallDisplayIcon={app}\{#MyAppExeName}

; Compression
Compression=lzma2
SolidCompression=yes

; Modern installer appearance
WizardStyle=modern

; Privileges - install for current user by default
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog

; License
LicenseFile=license.txt

; Minimum Windows version (Windows 10 1809)
MinVersion=10.0.17763

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "startupicon"; Description: "Start with Windows"; GroupDescription: "Startup:"; Flags: unchecked

[Files]
; Main application files
#if IsSelfContained
Source: "..\src\SendspinClient\bin\publish\win-x64-selfcontained\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
#else
Source: "..\src\SendspinClient\bin\publish\win-x64-framework\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
#endif

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon
Name: "{userstartup}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: startupicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: filesandordirs; Name: "{app}"

#if !IsSelfContained
[Code]
// Check if .NET 8.0 Desktop Runtime is installed (only for framework-dependent builds)
function IsDotNet8DesktopInstalled(): Boolean;
var
  ResultCode: Integer;
begin
  // Try to run dotnet --list-runtimes and check for Microsoft.WindowsDesktop.App 8.x
  Result := Exec('cmd.exe', '/c dotnet --list-runtimes | findstr "Microsoft.WindowsDesktop.App 8"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Result := Result and (ResultCode = 0);
end;

function InitializeSetup(): Boolean;
var
  ErrorCode: Integer;
  IsSilent: Boolean;
begin
  Result := True;
  IsSilent := WizardSilent();

  // Check for .NET 8 Desktop Runtime
  if not IsDotNet8DesktopInstalled() then
  begin
    // In silent mode, just fail without dialogs
    if IsSilent then
    begin
      Log('.NET 8.0 Desktop Runtime is not installed. Silent install cannot continue.');
      Log('Consider using the Self-Contained installer which includes .NET runtime.');
      Result := False;
      Exit;
    end;

    // Interactive mode - show dialog
    if MsgBox('.NET 8.0 Desktop Runtime is required but not installed.' + #13#10 + #13#10 +
              'Would you like to download it now?' + #13#10 + #13#10 +
              '(Tip: Use the "Self-Contained" installer to avoid this requirement)', mbConfirmation, MB_YESNO) = IDYES then
    begin
      ShellExec('open', 'https://dotnet.microsoft.com/download/dotnet/8.0/runtime', '', '', SW_SHOWNORMAL, ewNoWait, ErrorCode);
      MsgBox('Please install .NET 8.0 Desktop Runtime and run this installer again.', mbInformation, MB_OK);
      Result := False;
    end
    else
    begin
      MsgBox('Installation cannot continue without .NET 8.0 Desktop Runtime.', mbError, MB_OK);
      Result := False;
    end;
  end;
end;
#endif

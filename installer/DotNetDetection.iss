// .NET detection for the framework-dependent installer. Included from the [Code] section of
// Sendspin.Windows.iss, and from a detection-only harness when testing it.
//
// It mirrors how the app's x64 launcher (apphost) finds .NET, so setup only proceeds when the
// installed app will actually start. The launcher uses the first of:
//   1. DOTNET_ROOT_X64, then DOTNET_ROOT. Once set, it is used as-is with no fallback.
//   2. The registered x64 install location (HKLM, 32-bit registry view).
//   3. Program Files\dotnet, or Program Files\dotnet\x64 on ARM64 Windows.
// It then needs Microsoft.NETCore.App and Microsoft.WindowsDesktop.App 10.x under shared\.
//
// This replaces a check that piped `dotnet --list-runtimes` into findstr. That asked whichever
// dotnet was first on PATH, of any architecture and location. findstr also read the quoted words
// as alternatives, so a desktop runtime of any version passed, and setup completed for an app
// that could not start (issue #90).

function DotNetX64Root(): String;
var
  InstallLocation: String;
begin
  Result := GetEnv('DOTNET_ROOT_X64');
  if Result = '' then
    Result := GetEnv('DOTNET_ROOT');
  if Result <> '' then
    Exit;

  if RegQueryStringValue(HKLM32, 'SOFTWARE\dotnet\Setup\InstalledVersions\x64', 'InstallLocation', InstallLocation)
     and (InstallLocation <> '') then
  begin
    Result := InstallLocation;
    Exit;
  end;

  if IsArm64 then
    Result := ExpandConstant('{commonpf64}\dotnet\x64')
  else
    Result := ExpandConstant('{commonpf64}\dotnet');
end;

// True when Root\shared\Framework holds a 10.x version folder. The app asks for 10.0.0 with the
// default roll-forward, which accepts any 10.x and never 11.
function HasFramework10(const Root, Framework: String): Boolean;
var
  FindRec: TFindRec;
begin
  Result := False;
  if FindFirst(AddBackslash(Root) + 'shared\' + Framework + '\10.*', FindRec) then
  try
    repeat
      if (FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0 then
      begin
        Result := True;
        Exit;
      end;
    until not FindNext(FindRec);
  finally
    FindClose(FindRec);
  end;
end;

function IsDotNet10DesktopInstalled(): Boolean;
var
  Root: String;
begin
  Result := False;

  // The app is x64 only, and {commonpf64} is not available on 32-bit Windows.
  if not IsWin64 then
  begin
    Log('.NET check: 32-bit Windows cannot run the x64 app');
    Exit;
  end;

  Root := DotNetX64Root();
  Result := HasFramework10(Root, 'Microsoft.NETCore.App')
    and HasFramework10(Root, 'Microsoft.WindowsDesktop.App');

  if Result then
    Log('.NET check: found the .NET 10 x64 Desktop Runtime in ' + Root)
  else
    Log('.NET check: no .NET 10 x64 Desktop Runtime in ' + Root + ', where the app will look for it');
end;

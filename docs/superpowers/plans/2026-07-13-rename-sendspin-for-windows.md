# Sendspin for Windows Rename — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Rebrand the app from `WindowsSpin` / `SendspinClient` to **Sendspin for Windows** (display) with code identity `Sendspin.Windows`, without disrupting existing installed users, so the repo can later move to `Sendspin/sendspin-windows`.

**Architecture:** A wide but mechanical rename split into build-safe increments. Display strings and settings-folder migration land first (build stays green throughout), then the atomic code-identity rename (folders + projects + namespaces), then installer and CI/docs. The compiler, the existing xUnit suite, and grep "gate" commands are the safety net; the one piece of genuinely new logic — legacy AppData migration — gets a scripted manual smoke test.

**Tech Stack:** .NET 10 (`net10.0-windows10.0.17763.0`), WPF, xUnit, Inno Setup, GitHub Actions, Sendspin.SDK 9.1.0 (NuGet).

## Global Constraints

- Target framework stays `net10.0-windows10.0.17763.0`; SDK reference stays `Sendspin.SDK` `9.1.0` (unchanged).
- Naming targets (exact, context-dependent — `WindowsSpin` is NOT a single blanket replacement):
  - **Display text** (window title, product metadata, dialogs, logs, installer `AppName`/`AppAssocName`) → `Sendspin for Windows`
  - **Code identity** (namespaces, projects, solution, source folders, assembly, exe) → `Sendspin.Windows`
  - **AppData folder** (`AppPaths.AppName`) → `Sendspin for Windows`
  - **Toast AppId / installer `OutputBaseFilename` / CI artifact names** → `Sendspin.Windows`
  - **Company** (`Directory.Build.props`, `stylecop.json`) → `Sendspin`
- **URLs stay `github.com/chrisuthe/windowsSpin`** — repo transfer + URL rewrites are deferred (see Appendix A). Do NOT change any GitHub URL in this plan.
- Keep the installer `AppId` GUID `{{8E7F4A2B-5C3D-4E6F-9A1B-2C3D4E5F6A7B}` so existing installs upgrade in place.
- Leave unchanged: `SingleInstanceGuard` mutex/pipe names (`Sendspin_SingleInstance`, `Sendspin_ShowWindow`), the `Temp\Sendspin\artwork` path, Discord `ApplicationId`, and all Sendspin protocol identifiers.
- Work happens on branch `feat/rename-sendspin-for-windows` (already created). Commit after each task. Commit messages must not self-reference or add `Co-Authored-By`.

## File Structure

No new source files except the migration helper (added to the existing `AppPaths`). The rename touches:

- `Directory.Build.props`, `stylecop.json` — assembly/company metadata
- `SendspinClient.sln` → `Sendspin.Windows.sln`
- `src/SendspinClient/` → `src/Sendspin.Windows/` (+ `.csproj` rename)
- `src/SendspinClient.Services/` → `src/Sendspin.Windows.Services/` (+ `.csproj` rename)
- `tests/SendspinClient.Services.Tests/` → `tests/Sendspin.Windows.Services.Tests/` (+ `.csproj` rename)
- `src/Sendspin.Windows/Configuration/AppPaths.cs` — migration logic (the only new behavior)
- `src/Sendspin.Windows/App.xaml.cs` — migration call + display strings
- `src/Sendspin.Windows/MainWindow.xaml` — title/header text
- `src/Sendspin.Windows.Services/Notifications/WindowsToastNotificationService.cs` — toast AppId
- `installer/SendspinClient.iss` → `installer/Sendspin.Windows.iss`
- `.github/workflows/*.yml` — solution/project paths, artifact names
- `CLAUDE.md`, `README.md`, `CONTRIBUTING.md` — display name + stale path fixes
- `.gitignore` — ignore the stray workspace dir

---

### Task 1: Remove stray agent-workspace directory

The untracked `WindowSpin/` directory is an unrelated agent-workspace scaffold (`.workspace.json` with npm/cargo commands), not product code.

**Files:**
- Delete: `WindowSpin/` (untracked)
- Modify: `.gitignore`

- [ ] **Step 1: Confirm it is the stray scaffold, not product code**

Run: `cat "WindowSpin/.workspace.json"`
Expected: JSON with `"name": "WindowSpin"` and `verify_commands` referencing `npm`/`cargo` (proves it is not this .NET project).

- [ ] **Step 2: Delete it**

```bash
rm -rf "WindowSpin"
```

- [ ] **Step 3: Ignore it so a future agent tool re-creating it won't dirty the tree**

Append to `.gitignore`:

```gitignore

# Stray agent-workspace scaffold (not part of the product)
WindowSpin/
```

- [ ] **Step 4: Verify tree is clean**

Run: `git status --short`
Expected: no `WindowSpin/` entry; only the `.gitignore` modification.

- [ ] **Step 5: Commit**

```bash
git add .gitignore
git commit -m "chore: drop stray agent-workspace scaffold and ignore it"
```

---

### Task 2: Display name + assembly metadata (no identifier changes)

Pure string/metadata edits — namespaces are untouched, so the build stays green and the app still runs.

**Files:**
- Modify: `Directory.Build.props:13-15`
- Modify: `stylecop.json` (`companyName`)
- Modify: `src/SendspinClient/MainWindow.xaml:11` and `:138`
- Modify: `src/SendspinClient/App.xaml.cs:251,508,687,720`
- Modify: `src/SendspinClient.Services/Notifications/WindowsToastNotificationService.cs:52`

**Interfaces:**
- Produces: no code symbols; only user-visible strings and the toast AUMID string `"Sendspin.Windows"`.

- [ ] **Step 1: Update assembly metadata**

In `Directory.Build.props`, change:

```xml
    <Company>WindowsSpin</Company>
    <Product>WindowsSpin</Product>
    <Copyright>Copyright (c) 2024</Copyright>
```

to:

```xml
    <Company>Sendspin</Company>
    <Product>Sendspin for Windows</Product>
    <Copyright>Copyright (c) 2026</Copyright>
```

- [ ] **Step 2: Update StyleCop company name**

In `stylecop.json`, change `"companyName": "WindowsSpin",` to `"companyName": "Sendspin",`.

- [ ] **Step 3: Update the window title and header text**

In `src/SendspinClient/MainWindow.xaml`, change the `Title="WindowsSpin"` (line ~11) to `Title="Sendspin for Windows"`, and the header `<TextBlock Text="WindowsSpin" ...>` (line ~138) to `Text="Sendspin for Windows"`.

- [ ] **Step 4: Update product name, log lines, and error dialog**

In `src/SendspinClient/App.xaml.cs`:
- Line ~251: `ProductName = "Sendspin Windows Client",` → `ProductName = "Sendspin for Windows",`
- Line ~508: `Log.Information("WindowsSpin starting. ...` → `Log.Information("Sendspin for Windows starting. ...` (keep the rest of the format string and args identical)
- Line ~687: `Log.Information("WindowsSpin shutting down");` → `Log.Information("Sendspin for Windows shutting down");`
- Line ~720: the `"WindowsSpin Error"` dialog caption → `"Sendspin for Windows Error"`

- [ ] **Step 5: Update the toast AppId (AUMID)**

In `src/SendspinClient.Services/Notifications/WindowsToastNotificationService.cs`, line ~52, change `private const string AppId = "WindowsSpin";` to `private const string AppId = "Sendspin.Windows";`. (Leave the `Temp\Sendspin\artwork` path on line ~92 and the "Sendspin notifications" comment on line ~389 untouched.)

- [ ] **Step 6: Build**

Run: `dotnet build -c Release SendspinClient.sln`
Expected: Build succeeded, 0 errors.

- [ ] **Step 7: Startup smoke test (non-blocking — do NOT use `dotnet run`)**

This is a WPF tray app that never self-exits, so launch the built exe, give it time to start, then kill it. A near-immediate exit signals a startup crash.

```powershell
$exe = "src/SendspinClient/bin/Release/net10.0-windows10.0.17763.0/SendspinClient.exe"
$p = Start-Process $exe -PassThru
Start-Sleep -Seconds 8
if ($p.HasExited) { Write-Error "App exited early (code $($p.ExitCode)) - startup crash"; exit 1 }
Stop-Process -Id $p.Id -Force
"OK: app started and stayed running"
```

Expected: `OK: app started and stayed running`. (The visual title text is confirmed by the controller at the review gate.)

- [ ] **Step 8: Commit**

```bash
git add Directory.Build.props stylecop.json src/SendspinClient/MainWindow.xaml src/SendspinClient/App.xaml.cs src/SendspinClient.Services/Notifications/WindowsToastNotificationService.cs
git commit -m "feat: rebrand user-facing name to Sendspin for Windows"
```

---

### Task 3: AppData folder rename + legacy migration

Rename the settings folder and add a one-time, idempotent migration so existing users keep their settings and `client_id` (server pairing survives). The migration MUST run before the configuration is read.

**Files:**
- Modify: `src/SendspinClient/Configuration/AppPaths.cs`
- Modify: `src/SendspinClient/App.xaml.cs` (insert migration call at OnStartup)
- Modify: `src/SendspinClient/Configuration/ClientIdService.cs:13` (doc comment path)
- Modify: `src/SendspinClient/Configuration/LoggingSettings.cs:23,37` (doc comment paths)

**Interfaces:**
- Produces: `AppPaths.AppName` (now `"Sendspin for Windows"`), `AppPaths.LegacyAppName`, and `public static void AppPaths.MigrateLegacyDataIfNeeded()`.

- [ ] **Step 1: Rename the folder and add the migration method in `AppPaths.cs`**

Change line 15 and the class doc, then add the legacy path + migration. Replace lines 5-23 of `src/SendspinClient/Configuration/AppPaths.cs`:

```csharp
/// <summary>
/// Provides consistent paths for application data storage.
/// User settings and logs are stored in %LocalAppData%\Sendspin for Windows\ to ensure
/// write access regardless of installation location (e.g., Program Files).
/// </summary>
public static class AppPaths
{
    /// <summary>
    /// The application name used for folder naming.
    /// </summary>
    public const string AppName = "Sendspin for Windows";

    /// <summary>
    /// The pre-rebrand folder name, migrated from on first launch. See <see cref="MigrateLegacyDataIfNeeded"/>.
    /// </summary>
    public const string LegacyAppName = "WindowsSpin";

    /// <summary>
    /// Gets the user data directory for storing settings, logs, and other user-specific data.
    /// Located at %LocalAppData%\Sendspin for Windows\.
    /// </summary>
    public static string UserDataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        AppName);
```

Also update the two remaining doc-comment paths in the same file: line ~27 `%LocalAppData%\WindowsSpin\appsettings.json` → `%LocalAppData%\Sendspin for Windows\appsettings.json`, and line ~33 `%LocalAppData%\WindowsSpin\logs\` → `%LocalAppData%\Sendspin for Windows\logs\`.

- [ ] **Step 2: Add the migration method to `AppPaths.cs`**

Insert this method inside the `AppPaths` class (e.g. just after `DefaultSettingsPath`):

```csharp
    /// <summary>
    /// Migrates the pre-rebrand data directory (%LocalAppData%\WindowsSpin\) to the current
    /// location on first launch after the rename, preserving settings, client_id, and logs so
    /// server pairing survives. Idempotent: acts only when the new directory does not yet exist
    /// and the legacy directory does. Best-effort — runs before the logger is configured, so a
    /// failure (locked file, permissions) is swallowed and the app continues with a fresh directory.
    /// </summary>
    public static void MigrateLegacyDataIfNeeded()
    {
        try
        {
            string legacyDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                LegacyAppName);

            if (Directory.Exists(UserDataDirectory) || !Directory.Exists(legacyDir))
            {
                return;
            }

            Directory.Move(legacyDir, UserDataDirectory);
        }
        catch
        {
            // Intentionally best-effort: this runs before Serilog is configured (see OnStartup),
            // so there is no logger yet. A failed migration must not block startup.
        }
    }
```

- [ ] **Step 3: Call the migration first thing in `OnStartup`**

In `src/SendspinClient/App.xaml.cs`, insert the migration call after the single-instance guard succeeds and **before** `AppPaths.InitializeUserSettingsIfNeeded();` (currently line 75) — it must precede the `ConfigurationBuilder` that reads `AppPaths.UserSettingsPath`. Insert between line 72 and line 74:

```csharp
            });

        // Migrate legacy %LocalAppData%\WindowsSpin\ data to the renamed folder on first launch
        // after the rebrand (preserves settings, client_id, logs). Must run before config is read.
        AppPaths.MigrateLegacyDataIfNeeded();

        // Initialize user settings directory (copy defaults on first run)
        AppPaths.InitializeUserSettingsIfNeeded();
```

- [ ] **Step 4: Fix the stale doc-comment paths in the sibling config files**

- `src/SendspinClient/Configuration/ClientIdService.cs` line ~13: `%LocalAppData%\WindowsSpin\client_id` → `%LocalAppData%\Sendspin for Windows\client_id`
- `src/SendspinClient/Configuration/LoggingSettings.cs` lines ~23 and ~37: `%LocalAppData%\WindowsSpin\logs\` → `%LocalAppData%\Sendspin for Windows\logs\`

- [ ] **Step 5: Build**

Run: `dotnet build -c Release SendspinClient.sln`
Expected: Build succeeded, 0 errors.

- [ ] **Step 6: Migration smoke test (non-blocking launch-and-kill)**

Seed a fake legacy folder, launch the built exe just long enough for `OnStartup` to run the migration, kill it, then assert the move. Do NOT use `dotnet run` (the tray app never exits and would hang).

> **SAFETY — this test uses the REAL `%LocalAppData%` paths the app uses, because `Environment.SpecialFolder.LocalApplicationData` reads the shell known-folder (not the `%LOCALAPPDATA%` env var), so it cannot be redirected to a sandbox. Therefore the test MUST move any real data aside first and restore it in a `finally`. NEVER `Remove-Item -Recurse -Force` a real `%LocalAppData%` app folder — an earlier version of this step did exactly that and destroyed a real user's `client_id` and settings.**

```powershell
# Ensure no instance is running (the single-instance guard would short-circuit before migration)
Get-Process SendspinClient, "Sendspin.Windows" -ErrorAction SilentlyContinue | Stop-Process -Force

$legacy = "$env:LOCALAPPDATA\WindowsSpin"
$new    = "$env:LOCALAPPDATA\Sendspin for Windows"
$stamp  = Get-Date -Format 'yyyyMMdd-HHmmss'

# Move any REAL data aside so the test can never destroy it
$legacyBak = if (Test-Path $legacy) { $b = "$legacy.realbak-$stamp"; Rename-Item $legacy $b; $b } else { $null }
$newBak    = if (Test-Path $new)    { $b = "$new.realbak-$stamp";    Rename-Item $new $b;    $b } else { $null }

try {
    # Seed a FAKE legacy folder (real data is safely moved aside above)
    New-Item -ItemType Directory -Force $legacy | Out-Null
    Set-Content "$legacy\client_id" "TEST-CLIENT-ID-12345"
    Set-Content "$legacy\appsettings.json" '{"_marker":"legacy"}'

    $exe = "src/SendspinClient/bin/Release/net10.0-windows10.0.17763.0/SendspinClient.exe"
    $p = Start-Process $exe -PassThru
    Start-Sleep -Seconds 8
    Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue

    # Assertions
    if (Test-Path $legacy) { throw "FAIL: legacy folder still exists (not migrated)" }
    $id = Get-Content "$new\client_id"
    if ($id -ne "TEST-CLIENT-ID-12345") { throw "FAIL: client_id not preserved (got '$id')" }
    "OK: legacy folder migrated, client_id preserved"
}
finally {
    # Remove ONLY the test-created folders, then restore the real data
    Remove-Item -Recurse -Force $legacy -ErrorAction SilentlyContinue
    Remove-Item -Recurse -Force $new -ErrorAction SilentlyContinue
    if ($legacyBak) { Rename-Item $legacyBak $legacy }
    if ($newBak)    { Rename-Item $newBak $new }
}
```

Expected: `OK: legacy folder migrated, client_id preserved`, and your real `%LocalAppData%\WindowsSpin` / `Sendspin for Windows` folders are exactly as they were before the test. (Requires a Release build first — Step 5.)

- [ ] **Step 7: Commit**

```bash
git add src/SendspinClient/Configuration/AppPaths.cs src/SendspinClient/App.xaml.cs src/SendspinClient/Configuration/ClientIdService.cs src/SendspinClient/Configuration/LoggingSettings.cs
git commit -m "feat: migrate AppData folder to Sendspin for Windows on first launch"
```

---

### Task 4: Code-identity rename (folders, projects, solution, namespaces)

The atomic rename: `SendspinClient` → `Sendspin.Windows` across folders, project/solution files, namespaces, usings, and XAML `x:Class`/`clr-namespace`. This does not build green until every reference is consistent, so it is one task with a build+grep gate at the end.

**Files:** all of `src/`, `tests/`, and the solution file (see steps). Scope of the text replacement is `*.cs`, `*.xaml`, `*.csproj`, and the `.sln` only — the installer `.iss` is handled in Task 5 and docs/workflows in Task 6.

**Interfaces:**
- Produces: root namespaces `Sendspin.Windows` and `Sendspin.Windows.Services`; assembly/exe `Sendspin.Windows`; solution `Sendspin.Windows.sln`.

- [ ] **Step 1: Rename the source and test folders (preserving history)**

```bash
git mv src/SendspinClient src/Sendspin.Windows
git mv src/SendspinClient.Services src/Sendspin.Windows.Services
git mv tests/SendspinClient.Services.Tests tests/Sendspin.Windows.Services.Tests
```

- [ ] **Step 2: Rename the project and solution files**

```bash
git mv src/Sendspin.Windows/SendspinClient.csproj src/Sendspin.Windows/Sendspin.Windows.csproj
git mv src/Sendspin.Windows.Services/SendspinClient.Services.csproj src/Sendspin.Windows.Services/Sendspin.Windows.Services.csproj
git mv tests/Sendspin.Windows.Services.Tests/SendspinClient.Services.Tests.csproj tests/Sendspin.Windows.Services.Tests/Sendspin.Windows.Services.Tests.csproj
git mv SendspinClient.sln Sendspin.Windows.sln
```

- [ ] **Step 3: Replace the identifier in all code and project files**

Case-sensitive substring replace (handles `.Services` suffix, `x:Class`, `clr-namespace`, `ProjectReference` paths, and the `.sln` project names/paths in one pass):

```bash
grep -rlZ --include='*.cs' --include='*.xaml' --include='*.csproj' 'SendspinClient' src tests | xargs -0 sed -i 's/SendspinClient/Sendspin.Windows/g'
sed -i 's/SendspinClient/Sendspin.Windows/g' Sendspin.Windows.sln
```

Note: the SDK types `SendSpinClient` / `SendSpinHostService` have a capital `S` in "Spin" and do NOT match the case-sensitive pattern `SendspinClient` — they are left correct.

- [ ] **Step 4: Build the renamed solution**

Run: `dotnet build -c Release Sendspin.Windows.sln`
Expected: Build succeeded, 0 errors. If any error references a leftover `SendspinClient`, fix that file and rebuild.

- [ ] **Step 5: Grep gate — no identifier references remain**

Run: `grep -rn 'SendspinClient' src tests Sendspin.Windows.sln`
Expected: no output (exit 1). Any hit must be fixed before proceeding.

- [ ] **Step 6: Verify assembly/exe name**

Run: `ls src/Sendspin.Windows/bin/Release/net10.0-windows10.0.17763.0/Sendspin.Windows.exe`
Expected: the file exists (confirms the assembly renamed correctly).

- [ ] **Step 7: Run tests**

Run: `dotnet test Sendspin.Windows.sln -c Release`
Expected: all tests pass.

- [ ] **Step 8: Startup smoke test (non-blocking — catches XAML/`x:Class` mismatch)**

A namespace/`x:Class` mismatch throws at startup, not compile time, so launch the renamed exe and confirm it stays alive:

```powershell
Get-Process "Sendspin.Windows" -ErrorAction SilentlyContinue | Stop-Process -Force
$exe = "src/Sendspin.Windows/bin/Release/net10.0-windows10.0.17763.0/Sendspin.Windows.exe"
$p = Start-Process $exe -PassThru
Start-Sleep -Seconds 8
if ($p.HasExited) { Write-Error "App exited early (code $($p.ExitCode)) - likely XAML/x:Class mismatch"; exit 1 }
Stop-Process -Id $p.Id -Force
"OK: renamed app started and stayed running"
```

Expected: `OK: renamed app started and stayed running`.

- [ ] **Step 9: Commit**

```bash
git add -A
git commit -m "refactor: rename code identity SendspinClient to Sendspin.Windows"
```

---

### Task 5: Installer script

Rename the installer file and its identifiers to match the new exe/folder, keep the upgrade `AppId`, prefer the legacy settings folder in the post-install script (so the app can migrate it), and delete the orphaned old exe/shortcuts on upgrade.

**Files:**
- Rename: `installer/SendspinClient.iss` → `installer/Sendspin.Windows.iss`
- Modify: the renamed `.iss`

**Interfaces:**
- Consumes: publish output `src/Sendspin.Windows/bin/publish/win-x64-{selfcontained,framework}\Sendspin.Windows.exe` (from Task 4's project rename).

- [ ] **Step 1: Rename the installer script**

```bash
git mv installer/SendspinClient.iss installer/Sendspin.Windows.iss
```

- [ ] **Step 2: Update the app identity defines**

In `installer/Sendspin.Windows.iss`, change lines 4, 16, 17:

```inno
#define MyAppName "Sendspin for Windows"
```
```inno
#define MyAppExeName "Sendspin.Windows.exe"
#define MyAppAssocName "Sendspin for Windows"
```

Leave `#define MyAppURL "https://github.com/chrisuthe/windowsSpin"` (line 15) unchanged — URL rewrite is deferred.

- [ ] **Step 3: Update output filenames and publish source paths**

Change lines 39 and 41:

```inno
OutputBaseFilename=Sendspin.Windows-{#MyAppVersion}-Setup-SelfContained
```
```inno
OutputBaseFilename=Sendspin.Windows-{#MyAppVersion}-Setup
```

Change the icon path (line 43) and the two `Source:` publish paths (lines 73, 75), replacing `..\src\SendspinClient\` with `..\src\Sendspin.Windows\`:

```inno
SetupIconFile=..\src\Sendspin.Windows\Resources\Icons\sendspinTray.ico
```
```inno
Source: "..\src\Sendspin.Windows\bin\publish\win-x64-selfcontained\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
```
```inno
Source: "..\src\Sendspin.Windows\bin\publish\win-x64-framework\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
```

- [ ] **Step 4: Remove the orphaned old exe and shortcuts on upgrade**

The exe and shortcut names change, so an in-place upgrade would leave `SendspinClient.exe` and the old-named shortcuts behind. Add an `[InstallDelete]` section (place it just before `[Icons]`, after line 76):

```inno
[InstallDelete]
; Remove pre-rebrand exe and shortcuts left behind by an in-place upgrade
Type: files; Name: "{app}\SendspinClient.exe"
Type: files; Name: "{group}\WindowsSpin.lnk"
Type: files; Name: "{group}\Uninstall WindowsSpin.lnk"
Type: files; Name: "{autodesktop}\WindowsSpin.lnk"
Type: files; Name: "{userstartup}\WindowsSpin.lnk"
```

- [ ] **Step 5: Make the post-install settings script prefer the legacy folder**

So the installer does NOT pre-create the new folder and block the app's migration, point its settings directory at the legacy folder while that still exists. In the `DisableLoggingInUserSettings` PowerShell (lines ~102-103), replace:

```powershell
    '$settingsDir = "$env:LOCALAPPDATA\WindowsSpin"; ' +
    '$settingsPath = "$settingsDir\appsettings.json"; ' +
```

with:

```powershell
    '$legacyDir = "$env:LOCALAPPDATA\WindowsSpin"; ' +
    '$newDir = "$env:LOCALAPPDATA\Sendspin for Windows"; ' +
    '$settingsDir = if (Test-Path $newDir) { $newDir } elseif (Test-Path $legacyDir) { $legacyDir } else { $newDir }; ' +
    '$settingsPath = "$settingsDir\appsettings.json"; ' +
```

(The app's `MigrateLegacyDataIfNeeded` then moves the legacy folder — carrying this logging setting — into the new folder on next launch.)

- [ ] **Step 6: Validate the script references**

Run: `grep -nE "SendspinClient|WindowsSpin" installer/Sendspin.Windows.iss`
Expected: the only remaining matches are the intentional legacy references — the `[InstallDelete]` old-name entries, the `$legacyDir ...\WindowsSpin` line, and the `MyAppURL` (deferred). No `SendspinClient` remains except none (the exe/paths were updated).

- [ ] **Step 7: (If Inno Setup is installed) build the installer**

Run: `& "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" /DMyAppVersion=2.2.2 installer/Sendspin.Windows.iss`
Expected: compiles and emits `dist/Sendspin.Windows-2.2.2-Setup.exe`. (Requires a prior `dotnet publish` into `bin/publish/win-x64-framework`; if ISCC or the publish output is absent locally, skip and rely on the CI build after Task 6.)

- [ ] **Step 8: Commit**

```bash
git add installer/
git commit -m "build: update installer identity for Sendspin.Windows rename"
```

---

### Task 6: CI workflows and documentation

Update the build/publish/installer paths and artifact names in CI, and fix docs (display name + the stale `\Sendspin\` AppData paths in `CLAUDE.md`). URLs stay on the personal repo.

**Files:**
- Modify: `.github/workflows/ci.yml`, `.github/workflows/ci-sdk-dev.yml`, `.github/workflows/release.yml`
- Modify: `CLAUDE.md`, `README.md`, `CONTRIBUTING.md`

- [ ] **Step 1: Inventory the exact references to change**

Run: `grep -rnE "SendspinClient|WindowsSpin|windowsspin" .github/workflows`
Read every hit. They fall into two buckets: **paths/identifiers** (`SendspinClient.sln`, `src/SendspinClient/...csproj`, `installer/SendspinClient.iss`, `WindowsSpin-*` artifact names) and **the commented winget id** (`chrisuthe.WindowsSpin`).

- [ ] **Step 2: Update solution/project/installer paths and artifact names in workflows**

In all three workflow files, apply these replacements (paths/filenames — the code-identity form, no spaces):
- `SendspinClient.sln` → `Sendspin.Windows.sln`
- `src/SendspinClient/SendspinClient.csproj` → `src/Sendspin.Windows/Sendspin.Windows.csproj`
- `src/SendspinClient.Services/SendspinClient.Services.csproj` → `src/Sendspin.Windows.Services/Sendspin.Windows.Services.csproj`
- `tests/SendspinClient.Services.Tests/...` → `tests/Sendspin.Windows.Services.Tests/...`
- `installer/SendspinClient.iss` → `installer/Sendspin.Windows.iss`
- any `src/SendspinClient/bin/publish/...` → `src/Sendspin.Windows/bin/publish/...`
- artifact/asset base names `WindowsSpin-` → `Sendspin.Windows-` (matching the installer `OutputBaseFilename`)

Leave the display-name table cell in `ci-sdk-dev.yml` (the `| WindowsSpin |` summary row) as `| Sendspin for Windows |`.

Verify no path identifiers remain: `grep -rn "SendspinClient" .github/workflows` → expected empty.

- [ ] **Step 3: Leave the winget identifier for the transfer**

The commented `identifier: chrisuthe.WindowsSpin` in `release.yml` ties to the publisher account and winget listing, which change at transfer time. Leave it as-is; it is captured in Appendix A.

- [ ] **Step 4: Fix `CLAUDE.md` — stale AppData paths and names**

`CLAUDE.md` currently claims settings live under `%LOCALAPPDATA%\Sendspin\` — this was always wrong (the code used `\WindowsSpin\`). Update every `%LOCALAPPDATA%\Sendspin\...` occurrence to `%LOCALAPPDATA%\Sendspin for Windows\...` (settings, `logs`, log file path). Update the file-path table entries and prose that reference `src/SendspinClient/...` to `src/Sendspin.Windows/...`, and `SendspinClient.csproj`/`.sln` names accordingly. Change the project title/display references from `WindowsSpin` to `Sendspin for Windows`. Do NOT change GitHub URLs.

Verify: `grep -nE "LOCALAPPDATA\\\\Sendspin\\\\|src/SendspinClient|SendspinClient\.(csproj|sln)" CLAUDE.md` → expected empty.

- [ ] **Step 5: Update `README.md` and `CONTRIBUTING.md` display name**

Replace user-facing `WindowsSpin` with `Sendspin for Windows`. **Keep all `github.com/chrisuthe/windowsSpin` URLs unchanged** (including the `git clone`/`upstream` lines in `CONTRIBUTING.md`). Artifact/download filenames referenced in prose become `Sendspin.Windows-...` to match releases.

- [ ] **Step 6: Commit**

```bash
git add .github/workflows CLAUDE.md README.md CONTRIBUTING.md
git commit -m "docs: update CI paths, artifact names, and docs for Sendspin.Windows"
```

---

### Task 7: Full-solution verification

Final gates over the whole branch before it is ready to merge.

- [ ] **Step 1: Clean build**

Run: `dotnet build -c Release Sendspin.Windows.sln`
Expected: Build succeeded, 0 errors.

- [ ] **Step 2: Tests**

Run: `dotnet test Sendspin.Windows.sln -c Release`
Expected: all pass.

- [ ] **Step 3: Identifier gate across the whole repo (excluding history/build/docs-as-record)**

Run: `grep -rn 'SendspinClient' --include='*.cs' --include='*.xaml' --include='*.csproj' --include='*.sln' --include='*.iss' --include='*.yml' --include='*.md' . | grep -vE '/(bin|obj)/|docs/superpowers/'`
Expected: no output. (The spec/plan under `docs/superpowers/` intentionally record the before→after mapping and are excluded.)

- [ ] **Step 4: Display/identifier `WindowsSpin` gate**

Run: `grep -rn 'WindowsSpin' --include='*.cs' --include='*.xaml' --include='*.csproj' --include='*.iss' --include='*.yml' --include='*.md' . | grep -vE '/(bin|obj)/|docs/superpowers/'`
Expected: the ONLY remaining hits are intentional legacy references — `AppPaths.LegacyAppName`, the migration comments, and the installer `[InstallDelete]` / `$legacyDir` lines. Confirm each hit is one of these; anything else must be fixed.

- [ ] **Step 5: Manual in-place upgrade + migration check (if Inno Setup available)**

`dotnet publish src/Sendspin.Windows/Sendspin.Windows.csproj -c Release -r win-x64 --self-contained -o src/Sendspin.Windows/bin/publish/win-x64-selfcontained`, build the installer (Task 5 Step 7), then: with a seeded `%LOCALAPPDATA%\WindowsSpin\client_id`, install and launch. Expected: app launches as "Sendspin for Windows", the old `WindowsSpin` folder is gone, and the `client_id` is preserved in `%LOCALAPPDATA%\Sendspin for Windows\`.

- [ ] **Step 6: Confirm branch state**

Run: `git status && git log --oneline master..HEAD`
Expected: clean tree; commits from Tasks 1-6 present. The branch is ready for the user's validation window before merge.

---

## Appendix A — Deferred transfer checklist (do NOT run now)

Run only after the validation window, when moving the repo into the org:

1. Transfer `chrisuthe/windowsSpin` → `Sendspin/sendspin-windows` via GitHub repo transfer (preserves history/issues/PRs and sets up redirects).
2. Rewrite URLs to the new org path in: `README.md`, `CONTRIBUTING.md` (`git clone`/`upstream`), `installer/Sendspin.Windows.iss` (`MyAppURL`, and reconsider `MyAppPublisher`), and any workflow URL references.
3. Update the winget `identifier` comment in `release.yml` to the org's publisher (e.g. `Sendspin.SendspinForWindows`).
4. Update the local remote: `git remote set-url origin https://github.com/Sendspin/sendspin-windows.git`.

## Appendix B — Spec self-review

- **Spec coverage:** every spec section maps to a task — display name (T2), namespaces/projects/folders/assembly/exe (T4), AppData folder + migration (T3), installer + in-place upgrade + `[InstallDelete]` (T5), docs/`CLAUDE.md`/workflows (T6), housekeeping `WindowSpin/` (T1), deferred URLs/transfer (Appendix A). Verification criteria → T7.
- **Placeholder scan:** none — all steps carry literal edits, commands, and expected output.
- **Type consistency:** the single new symbol `AppPaths.MigrateLegacyDataIfNeeded()` is defined in T3 Step 2 and called in T3 Step 3 with the same signature; `AppPaths.LegacyAppName` defined in T3 Step 1 and referenced in T7 Step 4.

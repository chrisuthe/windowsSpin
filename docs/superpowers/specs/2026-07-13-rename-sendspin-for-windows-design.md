# Rename to "Sendspin for Windows" — Design Spec

Status: approved 2026-07-13. Prepares the project to move from the personal repo
(`chrisuthe/windowsSpin`) into the Sendspin org. The **code rename lands now**; the
**repo transfer and URL rewrites are deferred** until there is time for serious testing
and validation.

## Goal

Rebrand the application from `WindowsSpin` / `SendspinClient` to **Sendspin for Windows**,
with code identifiers aligned to the org's `Sendspin.SDK` convention, so the repo lands in
`Sendspin/sendspin-windows` already looking native to the org — without disrupting existing
installed users.

## The four naming layers

The current name is embedded in four separable layers. This spec renames all of them, but
sequences the repo/URL layer separately (see Sequencing).

| Layer | Current | Target |
|---|---|---|
| **Display name** — window title, toasts, installer, product metadata, dialogs, log lines | `WindowsSpin` | `Sendspin for Windows` |
| **Root namespaces** | `SendspinClient`, `SendspinClient.Services` | `Sendspin.Windows`, `Sendspin.Windows.Services` |
| **Solution / projects** | `SendspinClient.sln`, `SendspinClient.csproj`, `SendspinClient.Services.csproj`, `SendspinClient.Services.Tests.csproj` | `Sendspin.Windows.sln`, `Sendspin.Windows.csproj`, `Sendspin.Windows.Services.csproj`, `Sendspin.Windows.Services.Tests.csproj` |
| **Source folders** | `src/SendspinClient/`, `src/SendspinClient.Services/`, `tests/SendspinClient.Services.Tests/` | `src/Sendspin.Windows/`, `src/Sendspin.Windows.Services/`, `tests/Sendspin.Windows.Services.Tests/` |
| **Assembly / exe** | `SendspinClient.exe` | `Sendspin.Windows.exe` |
| **Assembly metadata** (`Directory.Build.props`) | `Company=WindowsSpin`, `Product=WindowsSpin` | `Company=Sendspin`, `Product=Sendspin for Windows` |
| **AppData folder** | `%LocalAppData%\WindowsSpin\` | `%LocalAppData%\Sendspin for Windows\` |
| **Toast AppId (AUMID)** | `WindowsSpin` | `Sendspin.Windows` |
| **StyleCop `companyName`** | `WindowsSpin` | `Sendspin` |
| **Repo / URLs** *(deferred to transfer)* | `github.com/chrisuthe/windowsSpin` | `github.com/Sendspin/sendspin-windows` |

### Left intentionally unchanged

- `SingleInstanceGuard` mutex/pipe names (`Sendspin_SingleInstance`, `Sendspin_ShowWindow`) —
  already org-neutral; renaming buys nothing and risks a transition-window collision.
- `Temp\Sendspin\artwork` scratch path — already org-neutral.
- Discord `ApplicationId` — a Discord-side identifier, unrelated to branding.
- Sendspin **protocol** field names and `client/hello` identity — protocol-level, not branding.
- The `Sendspin.SDK` package reference.

## Approach per component

### Code identifiers (namespaces, projects, folders, assembly)

- Rename the two source project folders and the test project folder on disk.
- Rename the `.csproj` / `.sln` files; update the `.sln` project paths and `ProjectReference`
  paths to match.
- Update `namespace` declarations and `using` statements across `.cs` files, plus
  `x:Class` / `clr-namespace:` references in `.xaml` files, from `SendspinClient*` to
  `Sendspin.Windows*`.
- Assembly name and root namespace follow the project name automatically once folders/projects
  are renamed; set them explicitly only if a build check shows a mismatch.
- Success gate: `grep -rI SendspinClient src tests` returns nothing.

### Display name

- Window title and header text (`MainWindow.xaml`), notification `AppId` and identity strings
  (`WindowsToastNotificationService.cs`), startup/shutdown/error log and dialog strings
  (`App.xaml.cs`), and `Company`/`Product` in `Directory.Build.props`.
- `stylecop.json` `companyName` — cosmetic; `xmlHeader: false` and the `copyrightText` do not
  interpolate the company name, so no per-file header edits are required.

### AppData folder + settings migration (must not orphan users)

- `AppPaths.AppName` changes `WindowsSpin` → `Sendspin for Windows`; the XML doc comments in
  `AppPaths.cs`, `ClientIdService.cs`, `LoggingSettings.cs` update to match.
- Add a **one-time, idempotent migration** in the `AppPaths` static init / first access: if the
  new folder does **not** exist but the legacy `%LocalAppData%\WindowsSpin\` **does**, move it
  (settings, `client_id`, logs) to the new location. This preserves the `client_id`, so a
  player keeps its identity and **server pairing survives the rename**. Runs only when the new
  folder is absent — safe on every subsequent launch.

### Installer (in-place upgrade for existing installs)

- Update `MyAppName` → `Sendspin for Windows`, `MyAppExeName` → `Sendspin.Windows.exe`,
  `MyAppAssocName`, `OutputBaseFilename` (both self-contained and framework variants), the
  publish source paths, and the uninstall settings-cleanup path (`\WindowsSpin` →
  `\Sendspin for Windows`).
- **Keep the existing `AppId` GUID** so a machine with the old `WindowsSpin` install upgrades
  in place rather than installing side by side.
- Add an `[InstallDelete]` section to remove the orphaned `SendspinClient.exe` and the
  old-named Start-menu / desktop / startup shortcuts, since the exe and shortcut names change.
- Toast notifications: changing the `AppId` const re-registers the AUMID via
  `ToastNotificationManagerCompat` on first send; no shortcut `AppUserModelID` is set today, so
  the const change is sufficient.

### Docs / housekeeping

- Fix stale `CLAUDE.md` paths that claim settings live under `\Sendspin\` (they never did — the
  code used `\WindowsSpin\`; post-rename they are `\Sendspin for Windows\`). Update the display
  name references in `CLAUDE.md`, `README.md`, `CONTRIBUTING.md`.
- Update workflow artifact names in `.github/workflows/*.yml` (`WindowsSpin-*` → the new base
  filename) and the commented winget identifier in `release.yml`.
- Delete the stray untracked `WindowSpin/` agent-workspace directory and add it to `.gitignore`.

## Sequencing

1. Do the entire code/display/AppData/installer rename on a feature branch in the **current
   personal repo**. **URLs stay pointing at `chrisuthe/windowsSpin`** so links keep working
   through the testing window.
2. Verify: clean Release build of the renamed solution, tests pass, installer builds, and an
   install over a simulated legacy `WindowsSpin` install upgrades in place and launches reading
   migrated settings (same `client_id`).
3. Merge to `master`.
4. **Deferred — the transfer checklist (run when validation is done):**
   - Transfer `chrisuthe/windowsSpin` → `Sendspin/sendspin-windows` via GitHub transfer
     (preserves history/issues/PRs, sets up URL redirects).
   - Rewrite repo URLs in `README.md`, `CONTRIBUTING.md`, `installer/*.iss`
     (`MyAppURL`/publisher), and any workflow references to the new org path.
   - Update the local `origin` remote to the new URL.

## Verification / success criteria

- `dotnet build -c Release Sendspin.Windows.sln` is green.
- `grep -rI SendspinClient src tests` returns nothing.
- `dotnet test` passes.
- Installer produces `Sendspin.Windows-{version}-Setup.exe` (and `-SelfContained`).
- Manual: install over a legacy `WindowsSpin` install → upgrades in place, launches, and the
  Stats/settings show the migrated `client_id` (pairing intact).

## Risks

- **Missed identifier reference** → build break. Mitigated by the `grep` success gate and a full
  Release build before merge.
- **Migration edge cases** — new folder partially present, or a locked file during move. The
  migration guards on new-folder-absent and should fail soft (log and continue with a fresh
  folder) rather than crash startup.
- **XAML/`x:Class` mismatches** — namespace changes in `.xaml` must match code-behind exactly, or
  the app fails at runtime, not compile time. Covered by a launch smoke test in verification.

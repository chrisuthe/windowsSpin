# CI: Build WindowsSpin against SDK source (Dev branch)

**Date:** 2026-06-04
**Status:** Approved (design)

## Problem

The Sendspin SDK is consumed as a published NuGet package (`Sendspin.SDK` `8.0.0`)
in both [`SendspinClient.csproj`](../../../src/SendspinClient/SendspinClient.csproj)
and [`SendspinClient.Services.csproj`](../../../src/SendspinClient.Services/SendspinClient.Services.csproj).

When SDK changes are still in flight (e.g. an open PR on
[Sendspin/sendspin-dotnet](https://github.com/Sendspin/sendspin-dotnet), such as PR #35),
there is no way to produce an installable WindowsSpin build that integrates those changes
without first publishing a new SDK package. We want an on-demand CI path that builds
WindowsSpin against the SDK **source** at an arbitrary ref (default: the `dev` branch),
producing signed, installable prereleases for hands-on testing.

## Goals

- Manually triggered build that compiles/links WindowsSpin against SDK source at a chosen ref.
- Produce the same artifact set as the existing `dev-build` job: signed framework-dependent
  and self-contained installers, plus portable ZIPs, published as a GitHub prerelease.
- Leave normal CI, local builds, and the release pipeline **completely unchanged**.
- Keep the SDK-dev prereleases in their own tag namespace so they never collide with or
  get cleaned up alongside normal `v0.0.0-dev.*` builds.

## Non-Goals

- No automatic triggering (no branch/push/PR triggers). Manual `workflow_dispatch` only.
- No local-feed NuGet packing. We use a direct ProjectReference swap.
- No support for SDK PRs originating from forks as the *common* case (an optional input
  covers it, but the default targets the canonical repo).

## Key Findings (informing the design)

- **SDK TFM compatibility:** the SDK multi-targets `net8.0;net10.0`. WindowsSpin targets
  `net10.0-windows10.0.17763.0`. A ProjectReference resolves to the SDK's `net10.0` build
  automatically — compatible.
- **No native assets:** the SDK's dependencies are all pure-managed
  (Concentus = C#-native Opus, Zeroconf, Makaretu.Dns, System.Text.Json,
  Microsoft.Extensions.Logging.Abstractions). There are **no `runtimes/` native DLLs** that
  a NuGet package would ship but a ProjectReference would drop. This is what makes the
  ProjectReference swap safe.
- **Assembly identity:** the SDK csproj declares `<Version>8.0.0</Version>` and
  `<PackageId>Sendspin.SDK</PackageId>` — identical to the published package. The
  ProjectReference produces the same `Sendspin.SDK` assembly identity, so no other project
  in the graph needs to be aware of the swap.
- **Both projects reference the SDK.** `SendspinClient.Services` and `SendspinClient` each
  have their own `Sendspin.SDK` PackageReference; both must be swapped, and both pointing at
  the *same* SDK project file means MSBuild builds the SDK once and shares it.
- **Repo is public, PR branches live in the canonical repo** — anonymous checkout works; the
  default `GITHUB_TOKEN` is sufficient.

## Design

### 1. Declarative reference swap in the csproj files

Replace the single unconditional `Sendspin.SDK` PackageReference in **each** project with a
pair of mutually-exclusive conditional ItemGroups, gated on an MSBuild property `UseSdkSource`:

```xml
<!-- Normal builds (default): consume the published NuGet package -->
<ItemGroup Condition="'$(UseSdkSource)' != 'true'">
  <PackageReference Include="Sendspin.SDK" Version="8.0.0" />
</ItemGroup>
<!-- SDK-source builds: reference the checked-out SDK project directly -->
<ItemGroup Condition="'$(UseSdkSource)' == 'true'">
  <ProjectReference Include="$(SdkSourcePath)/src/Sendspin.SDK/Sendspin.SDK.csproj" />
</ItemGroup>
```

- When `UseSdkSource` is unset/false (every existing build path), behavior is byte-for-byte
  the same as today.
- When `UseSdkSource=true`, the build references the SDK project at `$(SdkSourcePath)`.
- `SdkSourcePath` is supplied by the workflow (absolute path to the SDK checkout). It can also
  be supplied locally for developer builds against a local SDK clone.

This is preferred over a CI-time `sed`/PowerShell rewrite of the csproj because it is
declarative, reviewable, reverts cleanly, and is usable locally — at the cost of two small,
backward-compatible additions to committed project files.

### 2. New workflow: `.github/workflows/ci-sdk-dev.yml`

**Trigger:** `workflow_dispatch` with inputs:

| Input | Default | Purpose |
|-------|---------|---------|
| `sdk_ref` | `dev` | SDK branch/tag/SHA to build against |
| `sdk_repo` | `Sendspin/sendspin-dotnet` | Source repo (escape hatch for forks) |

**Permissions:** `contents: write` (prerelease), `id-token: write` (Azure OIDC) — same as `ci.yml`.

**Single job** mirroring the structure of the existing `dev-build` job in
[`ci.yml`](../../../.github/workflows/ci.yml), with these differences:

1. **Checkout WindowsSpin** (`actions/checkout@v4`).
2. **Checkout SDK** into `sdk-src/` (`actions/checkout@v4` with
   `repository: ${{ inputs.sdk_repo }}`, `ref: ${{ inputs.sdk_ref }}`, `path: sdk-src`).
3. **Setup .NET** `10.0.x`.
4. **Resolve SDK SHA** for labeling (short SHA of the SDK checkout).
5. **Generate version:** `0.0.0-sdkdev.<sdkShortSha>`; numeric `AssemblyVersion`/`FileVersion`
   = `0.0.0.0` (same purely-numeric constraint as dev-build).
6. **Restore + Build (Release)** with
   `-p:UseSdkSource=true -p:SdkSourcePath=${{ github.workspace }}/sdk-src`
   plus the version `-p:` args. Every subsequent `dotnet publish` carries the same flags.
7. **Publish** framework-dependent and self-contained to the same output folders the
   installer script expects.
8. **Sign application EXEs** (both flavors) via `azure/trusted-signing-action@v0` using the
   existing `AZURE_*` / `TRUSTED_SIGNING_*` secrets.
9. **Build installers** (Inno Setup, both `framework` and `selfcontained`), **sign installer
   EXEs**, **create portable ZIPs** — identical to dev-build.
10. **Clean up old SDK-dev prereleases:** `dev-drprasad/delete-older-releases@v0.3.4`,
    `keep_latest: 5`, `delete_tag_pattern: v0.0.0-sdkdev`. This pattern is disjoint from the
    `v0.0.0-dev` pattern used by `ci.yml`, so the two cleanups never touch each other's builds.
11. **Create prerelease:** `softprops/action-gh-release@v2`, `prerelease: true`,
    `tag_name: v0.0.0-sdkdev.<sdkShortSha>`,
    name like `SDK-Dev Build — app <appShortSha> / sdk <sdk_ref>@<sdkShortSha>`,
    body documenting both SHAs, the SDK ref/repo, and the standard "development build" warning.
    Attaches the two installers and two portable ZIPs.

### 3. Artifact naming

Reuses the existing `installer/SendspinClient.iss` and the dev-build artifact names, driven by
the `0.0.0-sdkdev.<sha>` version string. No installer-script changes required.

## Data Flow

```
workflow_dispatch (sdk_ref=dev, sdk_repo=Sendspin/sendspin-dotnet)
  → checkout windowsSpin  (./)
  → checkout SDK          (./sdk-src @ sdk_ref)
  → dotnet build/publish  -p:UseSdkSource=true -p:SdkSourcePath=<ws>/sdk-src
        └─ csproj conditional swaps PackageReference → ProjectReference(sdk-src/.../Sendspin.SDK.csproj)
  → sign EXEs → Inno Setup installers → sign installers → portable ZIPs
  → cleanup old v0.0.0-sdkdev.* (keep 5)
  → publish prerelease v0.0.0-sdkdev.<sdkShortSha>
```

## Risks & Mitigations

- **SDK API drift breaks the WindowsSpin build.** Expected and desirable — that's the signal
  this workflow exists to surface. The build simply fails with the compiler error; fix the
  client against the new SDK and re-run.
- **SDK adds a native/runtime dependency later.** Would not flow via ProjectReference the way a
  packed NuGet would. Mitigation: documented here; if the SDK ever gains native assets, revisit
  (likely switch that case to local pack). Not a concern for the current dependency set.
- **Prerelease clutter.** Bounded by the dedicated `keep_latest: 5` cleanup on the disjoint
  `v0.0.0-sdkdev` tag pattern.

## Verification

- Normal `ci.yml` build path unchanged: `dotnet build -c Release` with no `UseSdkSource`
  resolves the NuGet package exactly as before (verify locally before pushing).
- New workflow: trigger `ci-sdk-dev.yml` with `sdk_ref=dev`; confirm the run checks out the SDK,
  builds with the ProjectReference, signs, and publishes a `v0.0.0-sdkdev.*` prerelease whose
  release notes name both SHAs.
- Confirm a normal push still produces a `v0.0.0-dev.*` prerelease and that the two cleanup
  steps do not delete each other's releases.

## Files

- **New:** `.github/workflows/ci-sdk-dev.yml`
- **Edit:** `src/SendspinClient/SendspinClient.csproj` (conditional SDK reference)
- **Edit:** `src/SendspinClient.Services/SendspinClient.Services.csproj` (conditional SDK reference)
- **New:** this design doc

# Contributing to Action Wheel

Thanks for taking the time to contribute. This document covers how to get a
working build, the conventions the codebase follows, and what a good pull
request looks like. See also [AGENTS.md](AGENTS.md) for the terse reference
version of these rules and the [README](README.md) for a feature overview.

## Code of Conduct

This project follows a [Code of Conduct](CODE_OF_CONDUCT.md). By
participating you are expected to uphold it.

## Requirements

- Windows 10 version 1809 (build 17763) or later
- .NET 10.0 SDK
- Windows App SDK 2.4.0 (pinned in `Action Wheel.csproj`)
- Visual Studio 2022 with the "Windows application development" workload
  (for debugging with F5)

## Getting started

```powershell
git clone https://github.com/cmm-cmm/Action-Wheel.git
cd Action-Wheel
.\Build.ps1                # Debug build (the default)
```

`Build.ps1` is the single entry point for every build task — the same
commands run locally and in CI:

```powershell
.\Build.ps1                       # Debug build
.\Build.ps1 -Task Release
.\Build.ps1 -Task Publish         # self-contained single-file exe -> bin\Publish\win-x64
.\Build.ps1 -Task Package         # Publish, ZIP it, then launch the ZIP's exe as a smoke test
.\Build.ps1 -Task Installer       # x64 only; requires Inno Setup 6
.\Build.ps1 -Task VerifyBindings  # checks every classic {Binding} path against its type
.\Build.ps1 -Task VerifyApplication # eight isolated behavior-check programs
.\Build.ps1 -Task VerifyBuildSafety # cleanup/publish boundaries in temporary directories
.\Build.ps1 -Task VerifyGpu       # opt-in synthetic hardware D3D11 correctness checks
.\Build.ps1 -Task Clean -Preview # inspect compiler cleanup targets without deleting them
.\Build.ps1 -Task Clean
.\Build.ps1 -Task All
.\Build.ps1 -Task Debug -NoRestore # dependencies must already be restored for this project
```

In Visual Studio, open the solution and press F5 — there is exactly one
launch profile, **"Action Wheel (Unpackaged)"**. Quit the running app from
the tray before build, publish or actual cleanup. Verification tasks and
`Clean -Preview` do not replace the running program.

Cleanup uses a compiler-directory allowlist and preserves root
`bin\Publish`, `bin\Backups`, `bin\Checks`, `bin\Review` and unknown `bin`
children. Inspect `Clean -Preview` before cleanup. Publish output must be a
child directory of this repository's `bin\Publish`; the root itself,
external/linked paths and `quick-info-win-x64` runtime tree are rejected.
An existing selected candidate directory is cleared before publishing.

For a separate candidate, use
`-Task Publish -Output '.\bin\Publish\candidate-x64'`. `Package` and
`Installer` use the same output boundary. The x64-only installer receives
the exact publish path and the published EXE's product version; x86 and
ARM64 use `Publish` or `Package`. A successful publish is not deployment.

`-NoRestore` suppresses implicit restore on build/run/publish commands.
Restore each affected project before using it; the explicit `Restore`
task still performs a restore. See the [README build guide](README.md#build--run)
for the complete command boundaries.

## Project layout

Two assemblies: `ActionWheel.Core` (plain `net10.0`, no WinUI or `user32`,
namespace `Action_Wheel.Core`) holds the business logic; `Action Wheel` is
the WinUI 3 app on top of it. Application startup and the informational
window live in `App.xaml(.cs)` / `MainWindow.xaml(.cs)`. Global hooks,
action execution, configuration, and tray behavior live in `Services/`. The
radial overlay is in `Overlay/`; settings UI and editable view models are in
`Settings/` and `ViewModels/`. Packaging images are in `Assets/`.

## Coding style

- Four-space indentation in C# and XAML.
- PascalCase for types and public members, camelCase for locals and
  parameters, `_camelCase` for private fields.
- Nullable reference types are enabled — resolve warnings rather than
  suppressing them.
- Keep XAML and its code-behind paired; place new classes under the
  `Action_Wheel.<Folder>` namespace matching their folder.
- Preserve explanatory comments around Win32 hooks, transparency,
  threading, and focus behavior — these areas have non-obvious constraints
  that are easy to silently break. If you touch code with a comment
  explaining *why* something is done a certain way, read it before changing
  the *what*.

## Testing your change

Existing standalone checks live in `Tools/`. At minimum:

1. `dotnet build "Action Wheel.csproj"` must succeed with no new warnings.
2. Run the checks relevant to the change. `VerifyApplication` covers Quick
   Add, Note Board, mouse dispatch, ring gestures, Quick Info, reclaim
   settings, refresh timing and smoke-report status using isolated data.
   `VerifyBuildSafety` checks compiler cleanup and publish boundaries in
   disposable directories. Neither requires the depth model, shows
   windows, installs hooks or captures the desktop.
3. For GPU changes, run `VerifyGpu` explicitly on a hardware D3D11 device.
   It checks synthetic frames without model inference or desktop capture.
   Native-window smoke probes and `Package` have their own environment
   requirements and must be selected explicitly.
4. Manually verify affected Windows behavior: the configured opening
   gesture, Esc/outside-click dismissal, focus/input isolation, shortcut
   or launch dispatch, tray commands and settings persistence. Hidden or
   synthetic checks do not establish foreground or visual acceptance.
5. New checks should verify behavior and follow an existing standalone
   tool or a separate test project as appropriate.

The [Windows CI workflow](.github/workflows/checks.yml) compiles the main
app and runs binding, round-trip, chord, version, application and build-safety
checks without an interactive desktop or depth model. Hardware GPU behavior,
native window probes, packages and installers require separate validation.

## Commit and pull request guidelines

- Write short, imperative commit summaries (e.g. `Fix ring geometry clamp
  on secondary monitors`).
- Keep each commit focused; don't commit generated artifacts (`bin/`,
  `obj/`, publish/installer output — all covered by `.gitignore`).
- In the PR description: explain the user-visible behavior, list what you
  verified manually, link relevant issues, and include a screenshot or
  short recording for any overlay/settings UI change.
- Call out explicitly if your change touches hooks, manifests, publishing,
  or `actions.json` compatibility — these have the highest blast radius in
  this codebase.

## Reporting bugs and requesting features

Open a [GitHub issue](https://github.com/cmm-cmm/Action-Wheel/issues) with
steps to reproduce, what you expected, and what happened instead. For
security vulnerabilities, do **not** open a public issue — see
[SECURITY.md](SECURITY.md).

# Repository Guidelines

## Project Structure & Module Organization

Action Wheel is a .NET 10 WinUI 3 desktop application. Application startup and the informational window live in `App.xaml(.cs)` and `MainWindow.xaml(.cs)`. Keep global hooks, action execution, configuration, and tray behavior in `Services/`. The radial overlay belongs in `Overlay/`, while settings UI and editable view models belong in `Settings/`. Windows packaging images are stored in `Assets/`; deployment notes and design history are under `doc/`. Generated output in `bin/` and `obj/` must not be committed.

## Build, Test, and Development Commands

- `dotnet restore "Action Wheel.csproj"` restores Windows App SDK dependencies.
- `dotnet build "Action Wheel.csproj"` creates a Debug build and is the quickest compile check.
- `dotnet build "Action Wheel.csproj" -c Release` validates the Release configuration.
- `dotnet run --project "Action Wheel.csproj"` launches the unpackaged app locally on Windows.
- `.\Build.ps1 -Task Publish -Platform x64` publishes a self-contained executable to `bin\Publish\win-x64` (also supports `x86` and `ARM64`). `Build.ps1` is the current build entry point.

Visual Studio 2022 users can run the **Action Wheel (Unpackaged)** profile with F5. Keep `EnableMsixTooling` publish-only; the current build and publishing workflow is documented in the README's **Build & run** section and `Build.ps1`.

## Coding Style & Naming Conventions

Use four-space indentation in C# and XAML. Follow existing C# conventions: file-scoped responsibilities, PascalCase for types and public members, camelCase for locals and parameters, and `_camelCase` for private fields. Nullable reference types are enabled; resolve warnings instead of suppressing them casually. Keep XAML and code-behind paired, and place new classes in the `Action_Wheel.<Folder>` namespace. Preserve explanatory comments around Win32 hooks, transparency, threading, and focus behavior because these areas have non-obvious constraints.

## Testing Guidelines

Existing standalone checks live in `Tools/`; opt-in native smoke entry points are documented in `Doc/quick-add-and-noteboard.md`, `Doc/refresh-effect.md` and `Doc/prototypes/wheel-unified-glass-native.md`. There is no coverage threshold. Implementation changes must at least pass `dotnet build`. Manually verify affected Windows behavior: configured gesture opening, Esc/outside-click dismissal, shortcut or launch dispatch, tray commands, and settings persistence. Hidden checks do not establish foreground or visual acceptance. The application intentionally writes no activity or error logs. If adding tests, use behavior-focused names and follow the existing standalone-check structure or a separate test project as appropriate.

## Commit & Pull Request Guidelines

History uses short, imperative summaries such as `Refactor project structure and update metadata`. Keep each commit focused and avoid generated artifacts. Pull requests should explain user-visible behavior, list verification performed, link relevant issues, and include screenshots or a short recording for overlay/settings changes. Call out changes to hooks, manifests, publishing, or `actions.json` compatibility explicitly.

<#
.SYNOPSIS
    The one build script for Action Wheel: restore, build, publish, package, clean.

.DESCRIPTION
    Replaces Build-Publish.ps1 and Build-Quick.bat, which disagreed with each other and with the
    README about which flags a publish needs.

    Nothing here is interactive - no prompts, no "press any key" - so the same commands run on a
    developer's machine and on a CI runner. Every task exits non-zero on failure.

.PARAMETER Task
    Restore         - restore NuGet packages only
    Debug           - build the Debug configuration (the default)
    Release         - build the Release configuration
    Publish         - self-contained single-file executable in bin\Publish\win-<platform>
    Package         - Publish, ZIP the output without renaming the exe, then run a launch smoke test
    Installer       - Publish, then build the Inno Setup installer from ActionWheel-Setup.iss
    VerifyBindings  - check every classic {Binding} path against the type behind it
    VerifyRoundTrip - round-trip a probe ActionItem set through the actions.json and profile .xml
                      codecs and diff every field; catches a field added to ActionItem but missed
                      in one of the codecs
    VerifyChordGate - regression-check the two-button chord trigger's DOWN/UP swallow decisions
                      (ActionWheel.Core/ChordGate.cs), used by MouseHook
    VerifyVersionSync - check Action Wheel.csproj's <Version> against Package.appxmanifest's
                      Identity Version, which cannot be a project reference and so silently drifts
    VerifyFormat    - dotnet format --verify-no-changes against .editorconfig
    VerifyConfigBackup - regression-check ConfigBackup.TryImport's rejection paths (entry count,
                      entry size, unrecognised entry names, zip-slip)
    VerifyRefresh   - check refresh layers, radial timing, coloured bloom and highlight outlines
    Clean           - delete bin\ and obj\
    All             - Restore, VerifyBindings, VerifyRoundTrip, VerifyChordGate, VerifyVersionSync,
                      VerifyFormat, VerifyConfigBackup, VerifyRefresh, Debug, Release, Publish

.PARAMETER Platform
    x64 (default), x86 or ARM64. Sets both the MSBuild platform and the runtime identifier.

.EXAMPLE
    .\Build.ps1
    .\Build.ps1 -Task Publish
    .\Build.ps1 -Task Publish -Platform ARM64
#>

[CmdletBinding()]
param(
    [ValidateSet('Restore', 'Debug', 'Release', 'Publish', 'Package', 'Installer', 'VerifyBindings', 'VerifyRoundTrip', 'VerifyChordGate', 'VerifyVersionSync', 'VerifyFormat', 'VerifyConfigBackup', 'VerifyRefresh', 'Clean', 'All')]
    [string]$Task = 'Debug',

    [ValidateSet('x64', 'x86', 'ARM64')]
    [string]$Platform = 'x64',

    [string]$Output
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = $PSScriptRoot
$project = Join-Path $root 'Action Wheel.csproj'
$core = Join-Path $root 'ActionWheel.Core\ActionWheel.Core.csproj'
$rid = 'win-' + $Platform.ToLowerInvariant()

if (-not $Output) {
    $Output = Join-Path $root "bin\Publish\$rid"
}

function Write-Step([string]$text) {
    Write-Host ''
    Write-Host "==> $text" -ForegroundColor Cyan
}

# A running copy holds "Action Wheel.exe" open and the build fails deep inside MSBuild with
# MSB3026/MSB3027 - a copy error that says nothing about the app being open. Say it up front.
function Assert-NotRunning {
    $running = Get-Process -Name 'Action Wheel' -ErrorAction SilentlyContinue
    if ($running) {
        throw "Action Wheel is running (PID $($running.Id -join ', ')). Quit it from the tray icon, then build again."
    }
}

function Invoke-Dotnet([string[]]$dotnetArgs) {
    Write-Host "dotnet $($dotnetArgs -join ' ')" -ForegroundColor DarkGray
    & dotnet @dotnetArgs
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($dotnetArgs[0]) failed with exit code $LASTEXITCODE."
    }
}

function Invoke-Restore {
    Write-Step 'Restoring packages'
    Invoke-Dotnet @('restore', $project, '-r', $rid, "-p:Platform=$Platform")
}

function Invoke-Build([string]$configuration) {
    Write-Step "Building $configuration | $Platform"
    Assert-NotRunning

    # The core library is built as a project reference, but building it on its own first turns a
    # business-logic compile error into a two-line message instead of one buried under XAML output.
    Invoke-Dotnet @('build', $core, '-c', $configuration, '--nologo')
    Invoke-Dotnet @('build', $project, '-c', $configuration, '-r', $rid, "-p:Platform=$Platform", '--nologo')
}

function Invoke-VerifyVersionSync {
    Write-Step 'Verifying app version is in sync across csproj and Package.appxmanifest'

    $csprojContent = Get-Content -LiteralPath $project -Raw
    if ($csprojContent -notmatch '<Version>(?<v>[\d.]+)</Version>') {
        throw 'Could not find <Version> in "Action Wheel.csproj".'
    }
    $csprojVersion = $Matches['v']

    # Package.appxmanifest is not read by this unpackaged app at runtime, but it is not harmless
    # dead weight either: a future switch back to MSIX would ship whatever stale version sits
    # here, silently. It has no project-file mechanism to inherit the version from, unlike
    # AssemblyVersion/FileVersion/Version, which is exactly how it drifted to 2.4.0.0 while the
    # app moved on to 2.6.0 and 2.7.0 around it.
    $manifestPath = Join-Path $root 'Package.appxmanifest'
    $manifestContent = Get-Content -LiteralPath $manifestPath -Raw
    if ($manifestContent -notmatch 'Version="(?<v>[\d.]+)"\s*/>') {
        throw 'Could not find the Identity Version in Package.appxmanifest.'
    }
    $manifestVersion = $Matches['v']

    $expectedManifestVersion = "$csprojVersion.0"
    if ($manifestVersion -ne $expectedManifestVersion) {
        throw "Version mismatch: Action Wheel.csproj is $csprojVersion but Package.appxmanifest's " +
            "Identity Version is $manifestVersion (expected $expectedManifestVersion). " +
            "Update Package.appxmanifest before releasing."
    }

    Write-Host "csproj $csprojVersion / manifest $manifestVersion - in sync." -ForegroundColor Green
}

function Invoke-VerifyFormat {
    Write-Step 'Verifying formatting against .editorconfig'
    Invoke-Dotnet @('format', $project, '--verify-no-changes')
}

function Invoke-VerifyConfigBackup {
    Write-Step 'Verifying ConfigBackup.TryImport rejection paths'
    $tool = Join-Path $root 'Tools\ConfigBackupCheck\ConfigBackupCheck.csproj'
    Invoke-Dotnet @('run', '--project', $tool, '--nologo')
}

function Invoke-VerifyRefresh {
    Write-Step 'Verifying refresh analysis layers and radial cluster timing'
    $tool = Join-Path $root 'Tools\RefreshAnalyzerCheck\RefreshAnalyzerCheck.csproj'
    Invoke-Dotnet @('run', '--project', $tool, '--nologo')
    $bloomTool = Join-Path $root 'Tools\RefreshBloomCheck\RefreshBloomCheck.csproj'
    Invoke-Dotnet @('run', '--project', $bloomTool, '-c', 'Release', '--nologo')
    $outlineTool = Join-Path $root 'Tools\RefreshOutlineCheck\RefreshOutlineCheck.csproj'
    Invoke-Dotnet @('run', '--project', $outlineTool, '-c', 'Release', '--nologo')
}

function Invoke-Publish {
    Write-Step "Publishing Release | $rid"
    Assert-NotRunning
    Invoke-VerifyVersionSync

    if (Test-Path $Output) {
        Write-Host "Clearing $Output" -ForegroundColor DarkGray
        Remove-Item -LiteralPath $Output -Recurse -Force
    }

    # PublishTrimmed stays false on purpose: the settings window's row templates use classic
    # {Binding}, which resolves properties by reflection. Trimming would leave the rows blank.
    Invoke-Dotnet @(
        'publish', $project,
        '-c', 'Release',
        '-r', $rid,
        "-p:Platform=$Platform",
        '--self-contained', 'true',
        '-p:PublishSingleFile=true',
        '-p:PublishReadyToRun=true',
        '-p:PublishTrimmed=false',
        '-p:IncludeNativeLibrariesForSelfExtract=true',
        '-o', $Output,
        '--nologo'
    )

    $exe = Join-Path $Output 'Action Wheel.exe'
    if (Test-Path $exe) {
        $size = [math]::Round((Get-Item $exe).Length / 1MB, 1)
        Write-Host ''
        Write-Host "Action Wheel.exe  $size MB" -ForegroundColor Green
        Write-Host $Output -ForegroundColor Green
    }
}

function Invoke-Package {
    Invoke-Publish

    Write-Step "Packaging Release | $rid"

    $exe = Join-Path $Output 'Action Wheel.exe'
    if (-not (Test-Path -LiteralPath $exe)) {
        throw "Published executable was not found: $exe"
    }

    $version = (Get-Item -LiteralPath $exe).VersionInfo.ProductVersion
    if ([string]::IsNullOrWhiteSpace($version)) {
        throw 'The published executable has no ProductVersion.'
    }

    $packageDirectory = Join-Path $root 'bin\Packages'
    New-Item -ItemType Directory -Path $packageDirectory -Force | Out-Null
    $zip = Join-Path $packageDirectory "ActionWheel-v$version-$rid.zip"
    $checksum = $zip + '.sha256'

    if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
    if (Test-Path -LiteralPath $checksum) { Remove-Item -LiteralPath $checksum -Force }

    # WinUI's embedded resource index is tied to the published executable name. Renaming
    # "Action Wheel.exe" after publish makes Microsoft.UI.Xaml fail during startup, so the
    # version belongs on the archive and never on the executable inside it.
    $packageFiles = Get-ChildItem -LiteralPath $Output -File | Select-Object -ExpandProperty FullName
    Compress-Archive -LiteralPath $packageFiles -DestinationPath $zip -CompressionLevel Optimal

    $hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash
    Set-Content -LiteralPath $checksum -Value "$hash  $(Split-Path $zip -Leaf)" -Encoding ascii

    # Verify the distributable rather than the build folder: extract the ZIP, start the exact EXE
    # a user receives, and require it to stay alive long enough to have initialised WinUI and the
    # tray. This is what actually catches a single-file publish that builds fine but crashes on
    # launch (Microsoft.UI.Xaml.dll, 0xc000027b) - a build succeeding is not the same claim as the
    # exe running, and nothing before this step in the whole pipeline ever launches the result.
    $smokeDirectory = Join-Path ([System.IO.Path]::GetTempPath()) ("ActionWheelSmoke-" + [Guid]::NewGuid().ToString('N'))
    try {
        Expand-Archive -LiteralPath $zip -DestinationPath $smokeDirectory
        $smokeExe = Join-Path $smokeDirectory 'Action Wheel.exe'
        if (-not (Test-Path -LiteralPath $smokeExe)) {
            throw 'Package changed or omitted the required executable name Action Wheel.exe.'
        }

        $process = Start-Process -FilePath $smokeExe -PassThru -WindowStyle Hidden
        Start-Sleep -Seconds 6
        if ($process.HasExited) {
            throw "Packaged executable exited during smoke test (exit code $($process.ExitCode))."
        }

        Stop-Process -Id $process.Id -Force
        $process.WaitForExit()
    }
    finally {
        if (Test-Path -LiteralPath $smokeDirectory) {
            Remove-Item -LiteralPath $smokeDirectory -Recurse -Force
        }
    }

    Write-Host ''
    Write-Host $zip -ForegroundColor Green
    Write-Host "SHA-256 $hash" -ForegroundColor Green
}

function Invoke-Installer {
    Invoke-Publish

    Write-Step 'Building the installer'

    $iscc = @(
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
    ) | Where-Object { Test-Path $_ } | Select-Object -First 1

    if (-not $iscc) {
        throw 'Inno Setup 6 was not found. Install it, or run -Task Publish for the portable exe.'
    }

    & $iscc (Join-Path $root 'ActionWheel-Setup.iss')
    if ($LASTEXITCODE -ne 0) {
        throw "ISCC failed with exit code $LASTEXITCODE."
    }
}

function Invoke-VerifyBindings {
    Write-Step 'Verifying binding paths'
    & (Join-Path $root 'Verify-Bindings.ps1')
}

function Invoke-VerifyRoundTrip {
    Write-Step 'Verifying ActionItem round-trips through the actions.json and profile .xml codecs'
    $tool = Join-Path $root 'Tools\RoundTripCheck\RoundTripCheck.csproj'
    Invoke-Dotnet @('run', '--project', $tool, '--nologo')
}

function Invoke-VerifyChordGate {
    Write-Step 'Verifying the chord trigger DOWN/UP swallow decisions'
    $tool = Join-Path $root 'Tools\ChordGateCheck\ChordGateCheck.csproj'
    Invoke-Dotnet @('run', '--project', $tool, '--nologo')
}

function Invoke-Clean {
    Write-Step 'Cleaning'
    Assert-NotRunning

    foreach ($directory in @(
        'bin', 'obj', 'ActionWheel.Core\bin', 'ActionWheel.Core\obj',
        'Tools\RoundTripCheck\bin', 'Tools\RoundTripCheck\obj',
        'Tools\ChordGateCheck\bin', 'Tools\ChordGateCheck\obj',
        'Tools\RefreshAnalyzerCheck\bin', 'Tools\RefreshAnalyzerCheck\obj',
        'Tools\ConfigBackupCheck\bin', 'Tools\ConfigBackupCheck\obj'
    )) {
        $path = Join-Path $root $directory
        if (Test-Path $path) {
            Write-Host "Removing $directory" -ForegroundColor DarkGray
            Remove-Item -LiteralPath $path -Recurse -Force
        }
    }
}

switch ($Task) {
    'Restore' { Invoke-Restore }
    'Debug' { Invoke-Build 'Debug' }
    'Release' { Invoke-Build 'Release' }
    'Publish' { Invoke-Publish }
    'Package' { Invoke-Package }
    'Installer' { Invoke-Installer }
    'VerifyBindings' { Invoke-VerifyBindings }
    'VerifyRoundTrip' { Invoke-VerifyRoundTrip }
    'VerifyChordGate' { Invoke-VerifyChordGate }
    'VerifyVersionSync' { Invoke-VerifyVersionSync }
    'VerifyFormat' { Invoke-VerifyFormat }
    'VerifyConfigBackup' { Invoke-VerifyConfigBackup }
    'VerifyRefresh' { Invoke-VerifyRefresh }
    'Clean' { Invoke-Clean }
    'All' {
        Invoke-Restore
        Invoke-VerifyBindings
        Invoke-VerifyRoundTrip
        Invoke-VerifyChordGate
        Invoke-VerifyVersionSync
        Invoke-VerifyFormat
        Invoke-VerifyConfigBackup
        Invoke-VerifyRefresh
        Invoke-Build 'Debug'
        Invoke-Build 'Release'
        Invoke-Publish
    }
}

Write-Host ''
Write-Host "$Task completed." -ForegroundColor Green

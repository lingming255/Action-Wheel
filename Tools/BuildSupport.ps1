# Filesystem operations shared by Build.ps1 and its disposable-directory checks.
Set-StrictMode -Version Latest

function Get-WheelOwnedPath([string]$RepositoryRoot, [string]$Path) {
    $basePath = [IO.Path]::GetFullPath($RepositoryRoot).TrimEnd([IO.Path]::DirectorySeparatorChar)
    $fullPath = [IO.Path]::GetFullPath($Path, $basePath).TrimEnd([IO.Path]::DirectorySeparatorChar)
    $prefix = $basePath + [IO.Path]::DirectorySeparatorChar
    if (-not $fullPath.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Output must be a child of $basePath : $fullPath"
    }

    # A junction inside an apparently safe path can redirect a recursive delete elsewhere.
    $ancestor = $fullPath
    while ($ancestor.Length -ge $basePath.Length) {
        $item = Get-Item -LiteralPath $ancestor -Force -ErrorAction SilentlyContinue
        if ($null -ne $item -and ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "Output cannot pass through a symbolic link or junction: $ancestor"
        }
        if ($ancestor.Equals($basePath, [StringComparison]::OrdinalIgnoreCase)) { break }
        $ancestor = [IO.Path]::GetDirectoryName($ancestor)
    }
    return $fullPath
}

function Get-WheelPublishPath([string]$RepositoryRoot, [string]$Path) {
    $fullPath = Get-WheelOwnedPath $RepositoryRoot $Path
    $publishRoot = Join-Path ([IO.Path]::GetFullPath($RepositoryRoot)) 'bin\Publish'
    $publishPrefix = $publishRoot + [IO.Path]::DirectorySeparatorChar
    if (-not $fullPath.StartsWith($publishPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Publish output must be a subdirectory of this repository''s bin\Publish.'
    }
    $runtime = Join-Path $publishRoot 'quick-info-win-x64'
    if ($fullPath.Equals($runtime, [StringComparison]::OrdinalIgnoreCase) -or
        $fullPath.StartsWith($runtime + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Publish to a separate directory, then deploy reviewed files to quick-info-win-x64.'
    }
    if ((Test-Path -LiteralPath $fullPath) -and -not (Test-Path -LiteralPath $fullPath -PathType Container)) {
        throw "Publish output is a file: $fullPath"
    }
    return $fullPath
}

function Remove-WheelBuildTree([string]$RepositoryRoot, [string]$Path) {
    $fullPath = Get-WheelOwnedPath $RepositoryRoot $Path
    if (-not (Test-Path -LiteralPath $fullPath)) { return }
    # Refuse trees containing links as well as linked ancestors. No cleanup follows links.
    $links = @(Get-ChildItem -LiteralPath $fullPath -Recurse -Force -Attributes ReparsePoint -ErrorAction Stop)
    if ($links.Count -ne 0) { throw "Output contains a symbolic link or junction: $($links[0].FullName)" }
    Remove-Item -LiteralPath $fullPath -Recurse -Force
}

function Get-WheelCleanPaths([string]$RepositoryRoot) {
    $basePath = [IO.Path]::GetFullPath($RepositoryRoot)
    # bin also contains the installed copy, rollback snapshots and review evidence. Only known
    # compiler layouts are disposable; never clean the whole bin root or its unknown children.
    $relativePaths = @('obj', 'bin\x64', 'bin\x86', 'bin\ARM64', 'bin\Debug', 'bin\Release',
        'ActionWheel.Core\bin', 'ActionWheel.Core\obj')
    $toolsRoot = Join-Path $basePath 'Tools'
    if (Test-Path -LiteralPath $toolsRoot) {
        foreach ($directory in Get-ChildItem -LiteralPath $toolsRoot -Directory -Force) {
            if ($directory.Attributes -band [IO.FileAttributes]::ReparsePoint) { continue }
            if (@(Get-ChildItem -LiteralPath $directory.FullName -Filter '*.csproj' -File).Count -eq 0) { continue }
            $relativePaths += 'Tools\' + $directory.Name + '\bin'
            $relativePaths += 'Tools\' + $directory.Name + '\obj'
        }
    }
    foreach ($relativePath in $relativePaths) {
        Get-WheelOwnedPath $basePath (Join-Path $basePath $relativePath)
    }
}

function Clear-WheelBuildOutput([string]$RepositoryRoot, [switch]$Preview) {
    # Resolve and validate every target before the first mutation.
    $targets = @(Get-WheelCleanPaths $RepositoryRoot)
    foreach ($target in $targets) {
        if (-not (Test-Path -LiteralPath $target)) { continue }
        if ($Preview) { Write-Host "Would remove $target"; continue }
        Write-Host "Removing $target" -ForegroundColor DarkGray
        Remove-WheelBuildTree $RepositoryRoot $target
    }
}

# Exercise the same cleanup/publish helpers as Build.ps1, using only disposable files.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'BuildSupport.ps1')

$fixture = Join-Path ([IO.Path]::GetTempPath()) ('ActionWheelBuildSafety-' + [Guid]::NewGuid().ToString('N'))
$checks = 0
$junctions = @()

function Assert-Check([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
    $script:checks++
}

function Assert-Rejected([scriptblock]$Action, [string]$Message) {
    $rejected = $false
    try { & $Action | Out-Null } catch { $rejected = $true }
    Assert-Check $rejected $Message
}

function Add-FixtureFile([string]$RelativePath) {
    $path = Join-Path $fixture $RelativePath
    New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($path)) -Force | Out-Null
    Set-Content -LiteralPath $path -Value 'preserve-or-clean sentinel' -NoNewline
    return $path
}

try {
    New-Item -ItemType Directory -Path $fixture | Out-Null
    $preserved = @('bin\Publish\quick-info-win-x64\Action Wheel.exe', 'bin\Publish\previous\Action Wheel.exe',
        'bin\Backups\rollback\snapshot.json', 'bin\Checks\report.json', 'bin\Review\evidence.json',
        'bin\UnknownOutput\keep.txt', 'Tools\NotesOnly\bin\keep.txt', 'Assets\keep.txt')
    $compiled = @('bin\x64\Debug\compiled.dll', 'bin\Debug\compiled.dll', 'obj\project.assets.json',
        'ActionWheel.Core\bin\compiled.dll', 'ActionWheel.Core\obj\project.assets.json',
        'Tools\ExampleCheck\bin\compiled.dll', 'Tools\ExampleCheck\obj\project.assets.json')
    foreach ($file in @($preserved) + @($compiled)) { Add-FixtureFile $file | Out-Null }
    Add-FixtureFile 'Tools\ExampleCheck\ExampleCheck.csproj' | Out-Null

    Clear-WheelBuildOutput $fixture -Preview
    foreach ($file in $compiled) { Assert-Check (Test-Path -LiteralPath (Join-Path $fixture $file)) "Preview removed $file" }
    Clear-WheelBuildOutput $fixture
    foreach ($file in $compiled) { Assert-Check (-not (Test-Path -LiteralPath (Join-Path $fixture $file))) "Clean missed $file" }
    foreach ($file in $preserved) { Assert-Check (Test-Path -LiteralPath (Join-Path $fixture $file)) "Clean removed retained $file" }
    Assert-Check (Test-Path -LiteralPath (Join-Path $fixture 'Tools\ExampleCheck\ExampleCheck.csproj')) 'Clean removed source project.'

    $validOutput = Join-Path $fixture 'bin\Publish\candidate'
    Assert-Check ((Get-WheelPublishPath $fixture $validOutput) -eq $validOutput) 'Candidate publish output rejected.'
    Assert-Check ((Get-WheelPublishPath $fixture 'bin\Publish\relative') -eq (Join-Path $fixture 'bin\Publish\relative')) 'Relative output was not rooted in repository.'
    foreach ($invalid in @($fixture, (Join-Path $fixture 'bin\Publish'), (Join-Path $fixture 'bin\Publish\'),
        (Join-Path $fixture 'bin\Backups'), (Join-Path $fixture 'Assets'),
        (Join-Path $fixture 'bin\Publish\quick-info-win-x64'),
        (Join-Path $fixture 'bin\Publish\quick-info-win-x64\nested'),
        (Join-Path $fixture 'bin\Publish\..\Backups'), ($fixture + '-sibling\bin\Publish\candidate'))) {
        Assert-Rejected { Get-WheelPublishPath $fixture $invalid } "Unsafe publish output accepted: $invalid"
    }
    $fileOutput = Add-FixtureFile 'bin\Publish\file.txt'
    Assert-Rejected { Get-WheelPublishPath $fixture $fileOutput } 'A file was accepted as publish directory.'
    Assert-Rejected { Remove-WheelBuildTree $fixture $fixture } 'Repository root deletion accepted.'

    if ($IsWindows) {
        $retainedDirectory = Join-Path $fixture 'Assets'
        $junction = Join-Path $fixture 'bin\Publish\linked'
        New-Item -ItemType Junction -Path $junction -Target $retainedDirectory | Out-Null
        $junctions += $junction
        Assert-Rejected { Get-WheelPublishPath $fixture (Join-Path $junction 'candidate') } 'Publish accepted a junction ancestor.'
        Assert-Rejected { Remove-WheelBuildTree $fixture $junction } 'Cleanup accepted a junction target.'
        $nestedOutput = Join-Path $fixture 'bin\Publish\nested-links'
        New-Item -ItemType Directory -Path $nestedOutput | Out-Null
        $nestedJunction = Join-Path $nestedOutput 'redirect'
        New-Item -ItemType Junction -Path $nestedJunction -Target $retainedDirectory | Out-Null
        $junctions += $nestedJunction
        Assert-Rejected { Remove-WheelBuildTree $fixture $nestedOutput } 'Cleanup accepted a tree containing a junction.'
        Assert-Check (Test-Path -LiteralPath (Join-Path $retainedDirectory 'keep.txt')) 'Link rejection changed the target.'
    }

    Write-Host "Build filesystem safety: $checks checks passed." -ForegroundColor Green
}
finally {
    # Delete only the junction entries, never their targets, before the guarded fixture cleanup.
    foreach ($junction in $junctions) {
        $null = Get-WheelOwnedPath $fixture ([IO.Path]::GetDirectoryName($junction))
        if (Get-Item -LiteralPath $junction -Force -ErrorAction SilentlyContinue) {
            Remove-Item -LiteralPath $junction -Force
        }
    }
    Remove-WheelBuildTree ([IO.Path]::GetTempPath()) $fixture
}

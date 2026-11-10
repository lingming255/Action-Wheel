[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Image,
    [Parameter(Mandatory)][string]$Output
)
$ErrorActionPreference = 'Stop'
& dotnet run --project (Join-Path $PSScriptRoot 'RefreshDepthAnnotate.csproj') -c Release -- $Image $Output
if ($LASTEXITCODE -ne 0) { throw 'Depth annotation failed.' }

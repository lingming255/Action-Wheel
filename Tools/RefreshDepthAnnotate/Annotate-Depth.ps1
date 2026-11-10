[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Image,
    [Parameter(Mandatory)][string]$Output,
    [int]$OriginX,
    [int]$OriginY
)
$ErrorActionPreference = 'Stop'
if ($PSBoundParameters.ContainsKey('OriginX') -ne $PSBoundParameters.ContainsKey('OriginY')) {
    throw 'Supply both OriginX and OriginY, or omit both to use the image centre.'
}
$originArguments = @()
if ($PSBoundParameters.ContainsKey('OriginX')) { $originArguments = @($OriginX, $OriginY) }
& dotnet run --project (Join-Path $PSScriptRoot 'RefreshDepthAnnotate.csproj') -c Release -- $Image $Output @originArguments
if ($LASTEXITCODE -ne 0) { throw 'Depth annotation failed.' }

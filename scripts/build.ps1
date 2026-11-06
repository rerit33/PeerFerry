[CmdletBinding()]
param([string]$Version = '0.1.0', [switch]$SkipTests)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($Version -notmatch '^\d+\.\d+\.\d+(-[a-zA-Z0-9.-]+)?$') { throw 'Expected a semantic version.' }
$projectRoot = Split-Path -Parent $PSScriptRoot
Push-Location -LiteralPath $projectRoot
try {
    dotnet restore PeerFerry.sln
    if ($LASTEXITCODE -ne 0) { throw 'Restore failed.' }
    if (-not $SkipTests) {
        dotnet test tests/PeerFerry.Tests -c Release --no-restore --logger 'console;verbosity=minimal'
        if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
    }
    dotnet publish src/PeerFerry.App -c Release -r win-x64 --self-contained true -o dist/publish "-p:Version=$Version" -p:DebugType=None -p:DebugSymbols=false
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
    Copy-Item -LiteralPath LICENSE,THIRD-PARTY-NOTICES.md -Destination dist/publish
    dotnet tool restore
    if ($LASTEXITCODE -ne 0) { throw 'Tool restore failed.' }
    dotnet vpk pack --packId PeerFerry --packVersion $Version --packDir dist/publish --mainExe PeerFerry.exe --packTitle PeerFerry --packAuthors rerit33 --outputDir dist/releases --runtime win-x64 --shortcuts StartMenuRoot --skip-updates --yes
    if ($LASTEXITCODE -ne 0) { throw 'Packaging failed.' }
    $lines = Get-ChildItem -LiteralPath dist/releases -File | Where-Object { $_.Extension -in '.exe','.zip','.nupkg' } | ForEach-Object {
        $hash = Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256
        "$($hash.Hash.ToLowerInvariant())  $($_.Name)"
    }
    [IO.File]::WriteAllLines((Join-Path $projectRoot 'dist/releases/SHA256SUMS.txt'), [string[]]$lines)
    Write-Output "PeerFerry ${Version}: dist/releases"
}
finally { Pop-Location }

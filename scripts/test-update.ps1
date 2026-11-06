[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$testRoot = Join-Path $projectRoot ('.work/update-test-' + [Guid]::NewGuid().ToString('N'))
$feed = Join-Path $testRoot 'feed'
$instance = Join-Path $testRoot 'instance'
$publish = Join-Path $testRoot 'publish'
$result = Join-Path $testRoot 'result.json'
New-Item -ItemType Directory -Path $testRoot | Out-Null
Push-Location -LiteralPath $projectRoot
try {
    if (-not (Test-Path -LiteralPath dist/releases/PeerFerry-win-Portable.zip)) { throw 'Run scripts/build.ps1 first.' }
    Expand-Archive -LiteralPath dist/releases/PeerFerry-win-Portable.zip -DestinationPath $instance
    $fromVersion = [Version]([xml](Get-Content -LiteralPath Directory.Build.props -Raw)).Project.PropertyGroup.Version
    $toVersion = '{0}.{1}.{2}' -f $fromVersion.Major,$fromVersion.Minor,($fromVersion.Build + 1)
    dotnet publish src/PeerFerry.App -c Release -r win-x64 --self-contained true -o $publish "-p:Version=$toVersion" -p:DebugType=None -p:DebugSymbols=false
    if ($LASTEXITCODE -ne 0) { throw 'Test publish failed.' }
    dotnet vpk pack --packId PeerFerry --packVersion $toVersion --packDir $publish --mainExe PeerFerry.exe --outputDir $feed --runtime win-x64 --noInst --skip-updates --yes
    if ($LASTEXITCODE -ne 0) { throw 'Test packaging failed.' }
    $arguments = '--test-local-update "' + $feed + '" "' + $result + '"'
    $process = Start-Process -FilePath (Join-Path $instance 'PeerFerry.exe') -ArgumentList $arguments -WindowStyle Hidden -PassThru
    $deadline = [DateTime]::UtcNow.AddSeconds(50)
    while (-not (Test-Path -LiteralPath $result) -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 250 }
    if (-not (Test-Path -LiteralPath $result)) { throw "Update verification timed out: $testRoot" }
    $verification = Get-Content -LiteralPath $result -Raw | ConvertFrom-Json
    if (-not $verification.ok -or $verification.version -ne $toVersion -or $verification.assemblyVersion -ne ($toVersion + '.0')) { throw (Get-Content -LiteralPath $result -Raw) }
    Write-Output "PASS: real package download, apply and restart $fromVersion -> $toVersion. Evidence: $result"
}
finally { Pop-Location }

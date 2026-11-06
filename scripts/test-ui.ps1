[CmdletBinding()]
param([switch]$NoBuild, [string]$Executable)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
Push-Location -LiteralPath $projectRoot
try {
    if (-not $Executable) {
        if (-not $NoBuild) {
            dotnet build src/PeerFerry.App -c Release --no-restore
            if ($LASTEXITCODE -ne 0) { throw 'UI build failed.' }
        }
        $Executable = Join-Path $projectRoot 'src/PeerFerry.App/bin/Release/net10.0-windows/PeerFerry.exe'
    }
    $testRoot = Join-Path $projectRoot ('.work/ui-test-' + [Guid]::NewGuid().ToString('N'))
    $profile = Join-Path $testRoot 'profile'
    $shots = Join-Path $testRoot 'screenshots'
    New-Item -ItemType Directory -Path $testRoot | Out-Null
    $arguments = '--data-dir "' + $profile + '" --smoke-test "' + $shots + '"'
    $process = Start-Process -FilePath $Executable -ArgumentList $arguments -WindowStyle Hidden -PassThru
    if (-not $process.WaitForExit(60000)) { Stop-Process -Id $process.Id; throw "UI test timed out: $testRoot" }
    foreach ($errorFile in @('errors.log', 'smoke-error.txt')) {
        $errorPath = Join-Path $profile $errorFile
        if (Test-Path -LiteralPath $errorPath) { throw (Get-Content -LiteralPath $errorPath -Raw) }
    }
    if ($process.ExitCode -ne 0 -or -not (Test-Path -LiteralPath (Join-Path $shots 'result.txt'))) { throw "UI test failed: $testRoot" }
    Get-Content -LiteralPath (Join-Path $shots 'result.txt')
    Write-Output "Screenshots: $shots"
}
finally { Pop-Location }

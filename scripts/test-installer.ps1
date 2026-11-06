[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$testRoot = Join-Path $projectRoot ('.work/installer-test-' + [Guid]::NewGuid().ToString('N'))
$publish = Join-Path $testRoot 'publish'; $feed = Join-Path $testRoot 'feed'; $packages = Join-Path $testRoot 'packages'
$testId = 'PeerFerryInstallerTest'
$testTitle = 'PeerFerry Installer Test'
$desktop = [Environment]::GetFolderPath([Environment+SpecialFolder]::DesktopDirectory, [Environment+SpecialFolderOption]::Create)
$programs = [Environment]::GetFolderPath([Environment+SpecialFolder]::Programs, [Environment+SpecialFolderOption]::Create)
$desktopLink = Join-Path $desktop ($testTitle + '.lnk'); $menuLink = Join-Path $programs ($testTitle + '.lnk')
if ((Test-Path -LiteralPath $desktopLink) -or (Test-Path -LiteralPath $menuLink)) { throw 'A previous installer test shortcut already exists. It was not modified.' }
$registryPath = 'HKCU:/Software/Microsoft/Windows/CurrentVersion/Uninstall/' + $testId
if (Test-Path -LiteralPath $registryPath) { throw 'A previous installer test registration already exists. It was not modified.' }
New-Item -ItemType Directory -Path $testRoot | Out-Null
$currentInstall = $null
function Invoke-TestProcess([string]$File, [string]$Arguments, [int]$Timeout = 60000) {
    $process = Start-Process -FilePath $File -ArgumentList $Arguments -WindowStyle Hidden -PassThru
    if (-not $process.WaitForExit($Timeout)) { throw "Process timed out: $File" }
    if ($process.ExitCode -ne 0) { throw "Process failed ($($process.ExitCode)): $File. Evidence: $testRoot" }
}
function Assert-Shortcuts([bool]$DesktopExpected, [bool]$MenuExpected, [string]$InstallRoot) {
    if ((Test-Path -LiteralPath $desktopLink) -ne $DesktopExpected -or (Test-Path -LiteralPath $menuLink) -ne $MenuExpected) { throw "Wrong shortcut selection: desktop=$DesktopExpected, menu=$MenuExpected" }
    $shell = New-Object -ComObject WScript.Shell
    try {
        foreach ($linkPath in @($desktopLink,$menuLink)) {
            if (-not (Test-Path -LiteralPath $linkPath)) { continue }
            $shortcut = $shell.CreateShortcut($linkPath)
            $target = [IO.Path]::GetFullPath($shortcut.TargetPath)
            if (-not $target.StartsWith(([IO.Path]::GetFullPath($InstallRoot).TrimEnd('\') + '\'),[StringComparison]::OrdinalIgnoreCase) -or -not (Test-Path -LiteralPath $target)) { throw "Wrong shortcut target: $target" }
            if (-not $shortcut.IconLocation.StartsWith($target,[StringComparison]::OrdinalIgnoreCase)) { throw "Shortcut uses a different icon: $($shortcut.IconLocation)" }
        }
    }
    finally { [void][Runtime.InteropServices.Marshal]::ReleaseComObject($shell) }
}
Push-Location -LiteralPath $projectRoot
try {
    dotnet tool restore
    if ($LASTEXITCODE -ne 0) { throw 'Tool restore failed.' }
    $version = [Version]([xml](Get-Content Directory.Build.props -Raw)).Project.PropertyGroup.Version
    $nextVersion = '{0}.{1}.{2}' -f $version.Major,$version.Minor,($version.Build + 1)
    dotnet publish src/PeerFerry.App -c Release -r win-x64 --self-contained true -o $publish "-p:Version=$version" -p:DebugType=None -p:DebugSymbols=false
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
    dotnet vpk pack --packId $testId --packTitle $testTitle --packVersion $version --packDir $publish --mainExe PeerFerry.exe --outputDir $packages --runtime win-x64 --shortcuts Desktop,StartMenuRoot --icon src/PeerFerry.App/Assets/PeerFerry.ico --skip-updates --yes
    if ($LASTEXITCODE -ne 0) { throw 'Test packaging failed.' }
    $backend = Join-Path $packages ($testId + '-win-Setup.exe')
    $setup = Join-Path $testRoot 'PeerFerry-Test-Setup.exe'
    & ./scripts/build-installer.ps1 -BackendPath $backend -OutputPath $setup -Version $version -AppId $testId -Title $testTitle
    Invoke-TestProcess $setup ('--preview "' + (Join-Path $testRoot 'installer.png') + '"')
    # A different package ID/title keeps real PeerFerry installations, shortcuts and profiles untouched.
    foreach ($selection in @(@($true,$true), @($true,$false), @($false,$false), @($false,$true))) {
        $currentInstall = Join-Path $testRoot ('installed-' + [int]$selection[0] + [int]$selection[1])
        $arguments = '--silent --installto "' + $currentInstall + '" --desktop-shortcut ' + $(if ($selection[0]) {'yes'} else {'no'}) + ' --start-menu-shortcut ' + $(if ($selection[1]) {'yes'} else {'no'}) + ' --log "' + (Join-Path $testRoot 'install.log') + '"'
        Invoke-TestProcess $setup $arguments
        Assert-Shortcuts $selection[0] $selection[1] $currentInstall
        Write-Output "PASS: desktop=$($selection[0]), start menu=$($selection[1]), correct targets and application icons."
        if (-not $selection[0] -and $selection[1]) {
            dotnet publish src/PeerFerry.App -c Release -r win-x64 --self-contained true -o $publish "-p:Version=$nextVersion" -p:DebugType=None -p:DebugSymbols=false
            if ($LASTEXITCODE -ne 0) { throw 'Update publish failed.' }
            dotnet vpk pack --packId $testId --packTitle $testTitle --packVersion $nextVersion --packDir $publish --mainExe PeerFerry.exe --outputDir $feed --runtime win-x64 --shortcuts Desktop,StartMenuRoot --icon src/PeerFerry.App/Assets/PeerFerry.ico --noInst --skip-updates --yes
            if ($LASTEXITCODE -ne 0) { throw 'Update packaging failed.' }
            $result = Join-Path $testRoot 'update-result.json'
            $process = Start-Process -FilePath (Join-Path $currentInstall 'current/PeerFerry.exe') -ArgumentList ('--test-local-update "' + $feed + '" "' + $result + '"') -WindowStyle Hidden -PassThru
            $deadline = [DateTime]::UtcNow.AddSeconds(45)
            while (-not (Test-Path -LiteralPath $result) -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 200 }
            if (-not (Test-Path -LiteralPath $result)) { throw 'Installed update timed out.' }
            $verification = Get-Content -LiteralPath $result -Raw | ConvertFrom-Json
            if (-not $verification.ok -or $verification.version -ne $nextVersion) { throw (Get-Content -LiteralPath $result -Raw) }
            Assert-Shortcuts $false $true $currentInstall
            Write-Output 'PASS: real installed update/restart preserves the unchecked desktop shortcut.'
        }
        Invoke-TestProcess (Join-Path $currentInstall 'Update.exe') 'uninstall --silent'
        $currentInstall = $null
        if ((Test-Path -LiteralPath $desktopLink) -or (Test-Path -LiteralPath $menuLink)) { throw 'Uninstall left a test shortcut.' }
    }
    [IO.File]::WriteAllText((Join-Path $testRoot 'result.txt'), 'PASS: four shortcut selections, targets/icons, real update/restart, uninstall cleanup.')
    Write-Output "Installer evidence: $testRoot"
}
finally {
    if ($currentInstall -and (Test-Path -LiteralPath (Join-Path $currentInstall 'Update.exe'))) { Invoke-TestProcess (Join-Path $currentInstall 'Update.exe') 'uninstall --silent' }
    Pop-Location
}

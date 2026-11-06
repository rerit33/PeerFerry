[CmdletBinding()]
param([string]$AppPath)
$ErrorActionPreference = 'Stop'
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$workspace = Join-Path ([IO.Path]::GetTempPath()) ('PeerFerry-diagnostics-' + [Guid]::NewGuid().ToString('N'))
$report = Join-Path $workspace 'report'
$probe = Join-Path $workspace 'probe'
$start = Get-Date
New-Item -ItemType Directory -Force -Path $report,$probe | Out-Null
function Write-Report([string]$Name, $Value) {
    $text = ($Value | Out-String -Width 240)
    if ($env:USERPROFILE) { $text = $text.Replace($env:USERPROFILE,'[USERPROFILE]') }
    $text = [regex]::Replace($text,'(?i)peerferry://join/\S+','[INVITATION REDACTED]')
    [IO.File]::WriteAllText((Join-Path $report $Name), $text, [Text.Encoding]::UTF8)
}
try {
    $os = Get-ItemProperty 'HKLM:/SOFTWARE/Microsoft/Windows NT/CurrentVersion'
    $cpu = try { (Get-CimInstance Win32_Processor | Select-Object -First 1).Name } catch { $env:PROCESSOR_IDENTIFIER }
    Write-Report 'windows.txt' ([pscustomobject]@{
        ProductName=$os.ProductName; DisplayVersion=$os.DisplayVersion; ReleaseId=$os.ReleaseId
        Build=$os.CurrentBuildNumber; Revision=$os.UBR; RuntimeOS=[Environment]::OSVersion.VersionString
        OS64Bit=[Environment]::Is64BitOperatingSystem; PowerShell64Bit=[Environment]::Is64BitProcess
        CPU=$cpu; Architecture=$env:PROCESSOR_ARCHITECTURE; PowerShell=$PSVersionTable.PSVersion.ToString()
    } | Format-List)
    if (-not $AppPath) {
        $candidate = Join-Path $env:LOCALAPPDATA 'PeerFerry/current/PeerFerry.exe'
        if (Test-Path -LiteralPath $candidate) { $AppPath = $candidate }
        else {
            Add-Type -AssemblyName System.Windows.Forms
            $dialog = New-Object System.Windows.Forms.OpenFileDialog
            $dialog.Title = 'Выберите PeerFerry.exe (в папке current установленной или portable-версии)'
            $dialog.Filter = 'PeerFerry.exe|PeerFerry.exe'
            if ($dialog.ShowDialog() -ne 'OK') { throw 'Выбор приложения отменён.' }
            $AppPath = $dialog.FileName
        }
    }
    $AppPath = (Resolve-Path -LiteralPath $AppPath).Path
    if ([IO.Path]::GetFileName($AppPath) -ne 'PeerFerry.exe') { throw 'Нужен файл PeerFerry.exe.' }
    # Portable's root exe is a launcher. Inspect/run the real executable in current/.
    $folder = Split-Path -Parent $AppPath
    $currentExe = Join-Path $folder 'current/PeerFerry.exe'
    if (Test-Path -LiteralPath $currentExe) { $AppPath = $currentExe; $folder = Split-Path -Parent $AppPath }
    Write-Report 'application.txt' ([pscustomobject]@{
        Path=$AppPath; Version=[Diagnostics.FileVersionInfo]::GetVersionInfo($AppPath).FileVersion
        Files=(Get-ChildItem -LiteralPath $folder -File | Select-Object Name,Length | Format-Table -AutoSize | Out-String -Width 220)
    } | Format-List)
    $hashes = foreach ($name in @('PeerFerry.exe','PeerFerry.dll','PeerFerry.runtimeconfig.json','coreclr.dll','hostfxr.dll','hostpolicy.dll','wpfgfx_cor3.dll','PresentationFramework.dll','vcruntime140_cor3.dll','Velopack.dll')) {
        $path = Join-Path $folder $name
        if (Test-Path -LiteralPath $path) { Get-FileHash -LiteralPath $path -Algorithm SHA256 | Select-Object @{n='Name';e={$name}},Hash }
        else { [pscustomobject]@{Name=$name;Hash='MISSING'} }
    }
    Write-Report 'files-sha256.txt' ($hashes | Format-Table -AutoSize)
    $runtimeConfig = Join-Path $folder 'PeerFerry.runtimeconfig.json'
    if (Test-Path -LiteralPath $runtimeConfig) { Write-Report 'runtimeconfig.txt' (Get-Content -LiteralPath $runtimeConfig -Raw -Encoding UTF8) }
    Write-Host 'Проверяем запуск в отдельном тестовом профиле. Подождите до 30 секунд…'
    $info = New-Object Diagnostics.ProcessStartInfo
    $info.FileName=$AppPath; $info.WorkingDirectory=$folder; $info.UseShellExecute=$false; $info.CreateNoWindow=$true
    $info.RedirectStandardError=$true; $info.RedirectStandardOutput=$true
    $info.Arguments='--data-dir "' + (Join-Path $probe 'profile') + '" --smoke-test "' + (Join-Path $probe 'screenshots') + '"'
    $info.EnvironmentVariables['COREHOST_TRACE']='1'
    $info.EnvironmentVariables['COREHOST_TRACEFILE']=(Join-Path $report 'host-trace.log')
    $info.EnvironmentVariables['DOTNET_HOST_TRACE']='1'
    $info.EnvironmentVariables['DOTNET_HOST_TRACEFILE']=(Join-Path $report 'host-trace.log')
    $process=New-Object Diagnostics.Process; $process.StartInfo=$info
    try {
        if (-not $process.Start()) { throw 'Не удалось запустить диагностический экземпляр.' }
        $output=$process.StandardOutput.ReadToEndAsync(); $errors=$process.StandardError.ReadToEndAsync()
        $finished=$process.WaitForExit(30000)
        if (-not $finished) {
            # Only the isolated process started above; never stop the user's normal instance.
            $process.Kill(); $process.WaitForExit()
        }
        $exitCode=$process.ExitCode
        $exitHex='0x{0:X8}' -f ([BitConverter]::ToUInt32([BitConverter]::GetBytes([int]$exitCode),0))
        Write-Report 'launch.txt' ([pscustomobject]@{PID=$process.Id;FinishedWithin30Seconds=$finished;ExitCode=$exitCode;ExitCodeHex=$exitHex;Started=$start} | Format-List)
        Write-Report 'stdout.txt' $output.GetAwaiter().GetResult()
        Write-Report 'stderr.txt' $errors.GetAwaiter().GetResult()
    } catch { Write-Report 'launch-error.txt' $_.Exception.ToString() }
    finally { $process.Dispose() }
    foreach ($name in @('errors.log','startup.log','smoke-error.txt')) {
        $path=Join-Path $probe ('profile/' + $name)
        if (Test-Path -LiteralPath $path) { Write-Report ('probe-' + $name) (Get-Content -LiteralPath $path -Raw -Encoding UTF8) }
        $original=Join-Path $env:LOCALAPPDATA ('PeerFerry/' + $name)
        if (Test-Path -LiteralPath $original) { Write-Report ('existing-' + $name) (Get-Content -LiteralPath $original -Tail 100 -Encoding UTF8) }
    }
    $result=Join-Path $probe 'screenshots/result.txt'
    if (Test-Path -LiteralPath $result) { Write-Report 'ui-result.txt' (Get-Content -LiteralPath $result -Raw -Encoding UTF8) }
    foreach ($channel in @('Application','Microsoft-Windows-CodeIntegrity/Operational','Microsoft-Windows-Windows Defender/Operational')) {
        try {
            $events=Get-WinEvent -FilterHashtable @{LogName=$channel;StartTime=$start.AddMinutes(-10)} -ErrorAction Stop |
                Where-Object { $_.Message -match 'PeerFerry' } | Select-Object -First 15 TimeCreated,Id,ProviderName,Message
            Write-Report (($channel -replace '[^a-zA-Z0-9.-]','_') + '.txt') ($events | Format-List)
        } catch { Write-Report (($channel -replace '[^a-zA-Z0-9.-]','_') + '.txt') 'Нет доступных событий PeerFerry.' }
    }
} catch { Write-Report 'collector-error.txt' $_.Exception.ToString() }
finally {
    $hostTrace=Join-Path $report 'host-trace.log'
    if (Test-Path -LiteralPath $hostTrace) { Write-Report 'host-trace.log' ([IO.File]::ReadAllText($hostTrace)) }
    # Explicit report allowlist: no settings.json, identity.pfx, invitations or user files.
    $archive=Join-Path $PSScriptRoot ('PeerFerry-Diagnostics-' + $stamp + '.zip')
    try { Compress-Archive -Path (Join-Path $report '*') -DestinationPath $archive -Force }
    catch { $archive=Join-Path ([Environment]::GetFolderPath('Desktop')) ('PeerFerry-Diagnostics-' + $stamp + '.zip'); Compress-Archive -Path (Join-Path $report '*') -DestinationPath $archive -Force }
    Write-Host "Готово. Передайте этот ZIP для разбора ошибки: $archive"
}

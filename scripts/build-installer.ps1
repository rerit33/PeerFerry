[CmdletBinding()]
param([Parameter(Mandatory)][string]$BackendPath, [Parameter(Mandatory)][string]$OutputPath, [string]$Version = '0.2.1', [string]$AppId = 'PeerFerry', [string]$Title = 'PeerFerry')
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$backend = (Resolve-Path -LiteralPath $BackendPath).Path
$output = [IO.Path]::GetFullPath($OutputPath)
$buildRoot = Join-Path $projectRoot ('.work/installer-build-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $buildRoot | Out-Null
$payload = Join-Path $buildRoot 'backend.exe'; Copy-Item -LiteralPath $backend -Destination $payload
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$visualStudio = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $visualStudio) { throw 'MSVC x64 build tools and Windows SDK are required to build the installer.' }
$developerCommand = Join-Path $visualStudio 'Common7/Tools/VsDevCmd.bat'
$resourcePath = Join-Path $buildRoot 'Setup.rc'
$icon = (Join-Path $projectRoot 'src/PeerFerry.App/Assets/PeerFerry.ico').Replace('\','/')
$manifest = (Join-Path $projectRoot 'installer/Bootstrapper.manifest').Replace('\','/')
$rc = "101 ICON `"$icon`"`n102 RCDATA `"$($payload.Replace('\','/'))`"`n1 24 `"$manifest`"`n"
[IO.File]::WriteAllText($resourcePath, $rc)
$source = Join-Path $projectRoot 'installer/Bootstrapper.cpp'
$object = Join-Path $buildRoot 'Setup.obj'; $resource = Join-Path $buildRoot 'Setup.res'
# Arguments are passed through a generated command file, with no user-controlled shell fragments.
foreach ($value in @($Version,$AppId,$Title)) { if ($value -notmatch '^[a-zA-Z0-9. -]+$') { throw 'Unexpected installer metadata.' } }
$metadataPath = Join-Path $buildRoot 'Metadata.h'
[IO.File]::WriteAllText($metadataPath, "#define APP_ID L`"$AppId`"`n#define APP_TITLE L`"$Title`"`n#define APP_VERSION L`"$Version`"`n")
$compile = @"
@call "$developerCommand" -arch=x64 -host_arch=x64 >nul
@if errorlevel 1 exit /b 1
rc.exe /nologo /fo "$resource" "$resourcePath"
@if errorlevel 1 exit /b 1
cl.exe /nologo /std:c++17 /utf-8 /O2 /MT /EHsc /W4 /FI"$metadataPath" /Fo"$object" "$source" "$resource" /Fe"$output" /link /SUBSYSTEM:WINDOWS /MANIFEST:NO /DYNAMICBASE /NXCOMPAT /HIGHENTROPYVA user32.lib gdi32.lib shell32.lib ole32.lib shlwapi.lib comctl32.lib gdiplus.lib
@exit /b %errorlevel%
"@
$commandFile = Join-Path $buildRoot 'compile.cmd'; [IO.File]::WriteAllText($commandFile,$compile,[Text.Encoding]::Default)
& $env:ComSpec /d /c "`"$commandFile`""
if ($LASTEXITCODE -ne 0) { throw 'Native installer compilation failed.' }
Write-Output "Installer: $output"

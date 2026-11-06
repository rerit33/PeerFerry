[CmdletBinding()]
param([Parameter(Mandatory)][string]$Executable)
$ErrorActionPreference = 'Stop'
$path = (Resolve-Path -LiteralPath $Executable).Path
$stream = [IO.File]::OpenRead($path)
$pe = [System.Reflection.PortableExecutable.PEReader]::new($stream)
try {
    if ($pe.PEHeaders.CoffHeader.Machine -ne [System.Reflection.PortableExecutable.Machine]::Amd64) { throw 'Expected a Windows x64 apphost.' }
    # CET is an extended DLL-characteristic bit in a type-20 PE debug-directory entry.
    $flags = 0
    foreach ($entry in $pe.ReadDebugDirectory()) {
        if ([int]$entry.Type -ne 20) { continue }
        if ($entry.DataSize -lt 4) { throw 'Invalid extended DLL characteristics entry.' }
        $stream.Position = $entry.DataPointer
        $bytes = [byte[]]::new(4)
        if ($stream.Read($bytes, 0, 4) -ne 4) { throw 'Truncated extended DLL characteristics entry.' }
        $flags = $flags -bor [BitConverter]::ToUInt32($bytes, 0)
    }
    if (($flags -band 1) -ne 0) { throw 'CET_COMPAT is still enabled: older Windows 10 builds can fail before Main.' }
    $standardFlags = [int]$pe.PEHeaders.PEHeader.DllCharacteristics
    if (($standardFlags -band 0x140) -ne 0x140) { throw 'The apphost must retain ASLR and DEP.' }
    Write-Output 'PASS: x64 apphost has CET_COMPAT disabled and retains ASLR/DEP.'
}
finally { $pe.Dispose(); $stream.Dispose() }

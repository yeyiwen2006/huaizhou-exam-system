param(
    [Parameter(Mandatory = $true)][string]$SourcePath,
    [Parameter(Mandatory = $true)][string]$WalFileName,
    [Parameter(Mandatory = $true)][string]$ArchiveRoot
)

$ErrorActionPreference = 'Stop'
$source = [System.IO.Path]::GetFullPath($SourcePath)
if (-not [System.IO.File]::Exists($source)) {
    throw "WAL source does not exist: $source"
}
if (([System.IO.File]::GetAttributes($source) -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
    throw 'WAL source must not be a reparse point.'
}

if ([string]::IsNullOrWhiteSpace($WalFileName) -or $WalFileName -eq '.' -or $WalFileName -eq '..' -or
    [System.IO.Path]::GetFileName($WalFileName) -ne $WalFileName -or
    [System.IO.Path]::IsPathRooted($WalFileName)) {
    throw 'WAL file name must not contain a directory component.'
}

$archivePathRoot = [System.IO.Path]::GetPathRoot($ArchiveRoot)
if ([string]::IsNullOrWhiteSpace($archivePathRoot) -or
    $archivePathRoot -eq [string][System.IO.Path]::DirectorySeparatorChar -or
    -not $archivePathRoot.EndsWith([string][System.IO.Path]::DirectorySeparatorChar)) {
    throw 'WAL archive root must be a fully qualified path.'
}
$root = [System.IO.Path]::GetFullPath($ArchiveRoot)

[System.IO.Directory]::CreateDirectory($root) | Out-Null
if (([System.IO.File]::GetAttributes($root) -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
    throw 'WAL archive root must not be a reparse point.'
}
$target = [System.IO.Path]::GetFullPath([System.IO.Path]::Combine($root, $WalFileName))
$rootPrefix = $root
if (-not $rootPrefix.EndsWith([string][System.IO.Path]::DirectorySeparatorChar)) {
    $rootPrefix += [System.IO.Path]::DirectorySeparatorChar
}
if (-not $target.StartsWith($rootPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw 'WAL archive target escapes the configured archive root.'
}

function Test-SameFileContent([string]$Left, [string]$Right) {
    $leftInfo = [System.IO.FileInfo]::new($Left)
    $rightInfo = [System.IO.FileInfo]::new($Right)
    if ($leftInfo.Length -ne $rightInfo.Length) { return $false }
    $leftHash = (Get-FileHash -LiteralPath $Left -Algorithm SHA256).Hash
    $rightHash = (Get-FileHash -LiteralPath $Right -Algorithm SHA256).Hash
    return [string]::Equals($leftHash, $rightHash, [StringComparison]::OrdinalIgnoreCase)
}

if ([System.IO.File]::Exists($target)) {
    if (([System.IO.File]::GetAttributes($target) -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'Existing WAL archive target must not be a reparse point.'
    }
    if (-not (Test-SameFileContent $source $target)) {
        throw 'Existing WAL archive has the same name but different content.'
    }
    exit 0
}

$temporary = [System.IO.Path]::Combine(
    $root,
    ".$WalFileName.$PID.$([Guid]::NewGuid().ToString('N')).tmp")
try {
    $input = [System.IO.File]::Open($source, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::Read)
    try {
        $output = [System.IO.File]::Open($temporary, [System.IO.FileMode]::CreateNew, [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)
        try {
            $input.CopyTo($output)
            $output.Flush($true)
        }
        finally { $output.Dispose() }
    }
    finally { $input.Dispose() }

    if (-not (Test-SameFileContent $source $temporary)) {
        throw 'Temporary WAL archive does not match the source.'
    }

    try {
        [System.IO.File]::Move($temporary, $target)
    }
    catch [System.IO.IOException] {
        if (-not [System.IO.File]::Exists($target) -or
            -not (Test-SameFileContent $source $target)) {
            throw
        }
    }
}
finally {
    if ([System.IO.File]::Exists($temporary)) {
        [System.IO.File]::Delete($temporary)
    }
}

exit 0

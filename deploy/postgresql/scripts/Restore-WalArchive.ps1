param(
    [Parameter(Mandatory = $true)][string]$WalFileName,
    [Parameter(Mandatory = $true)][string]$DestinationPath,
    [Parameter(Mandatory = $true)][string]$ArchiveRoot
)

$ErrorActionPreference = 'Stop'
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
if (-not [System.IO.Directory]::Exists($root)) {
    exit 1
}
if (([System.IO.File]::GetAttributes($root) -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
    throw 'WAL archive root must not be a reparse point.'
}

$source = [System.IO.Path]::GetFullPath([System.IO.Path]::Combine($root, $WalFileName))
$rootPrefix = $root
if (-not $rootPrefix.EndsWith([string][System.IO.Path]::DirectorySeparatorChar)) {
    $rootPrefix += [System.IO.Path]::DirectorySeparatorChar
}
if (-not $source.StartsWith($rootPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw 'WAL restore source escapes the configured archive root.'
}
if (-not [System.IO.File]::Exists($source)) {
    exit 1
}
if (([System.IO.File]::GetAttributes($source) -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
    throw 'WAL archive files must not be reparse points.'
}

$destination = [System.IO.Path]::GetFullPath($DestinationPath)
if ([string]::Equals($source, $destination, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw 'WAL restore destination must differ from the archive source.'
}

function Test-SameFileContent([string]$Left, [string]$Right) {
    $leftInfo = [System.IO.FileInfo]::new($Left)
    $rightInfo = [System.IO.FileInfo]::new($Right)
    if ($leftInfo.Length -ne $rightInfo.Length) { return $false }
    $leftHash = (Get-FileHash -LiteralPath $Left -Algorithm SHA256).Hash
    $rightHash = (Get-FileHash -LiteralPath $Right -Algorithm SHA256).Hash
    return [string]::Equals($leftHash, $rightHash, [System.StringComparison]::OrdinalIgnoreCase)
}

if ([System.IO.File]::Exists($destination)) {
    if (Test-SameFileContent $source $destination) { exit 0 }
    throw 'Existing WAL restore destination has different content.'
}

$destinationDirectory = [System.IO.Path]::GetDirectoryName($destination)
if ([string]::IsNullOrWhiteSpace($destinationDirectory) -or
    -not [System.IO.Directory]::Exists($destinationDirectory)) {
    throw 'WAL restore destination directory does not exist.'
}

$temporary = [System.IO.Path]::Combine(
    $destinationDirectory,
    ".$([System.IO.Path]::GetFileName($destination)).$PID.$([Guid]::NewGuid().ToString('N')).tmp")
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
        throw 'Temporary restored WAL does not match the archive source.'
    }
    [System.IO.File]::Move($temporary, $destination)
}
finally {
    if ([System.IO.File]::Exists($temporary)) {
        [System.IO.File]::Delete($temporary)
    }
}

exit 0

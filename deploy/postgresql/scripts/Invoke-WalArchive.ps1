param(
    [Parameter(Mandatory = $true)][string]$SourcePath,
    [Parameter(Mandatory = $true)][string]$WalFileName,
    [Parameter(Mandatory = $true)][string]$ArchiveRoot
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'Postgresql.Operations.psm1') -Force -DisableNameChecking
$source = Assert-ControlledPath ([System.IO.Path]::GetFullPath($SourcePath))
if (-not [System.IO.File]::Exists($source)) {
    throw "WAL source does not exist: $source"
}
if (([System.IO.File]::GetAttributes($source) -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
    throw 'WAL source must not be a reparse point.'
}

# Promotion may archive the old timeline's final, incomplete segment as .partial.
if ($WalFileName -notmatch '^(?:[0-9A-F]{24}(?:\.partial|\.[0-9A-F]{8}\.backup)?|[0-9A-F]{8}\.history)$') {
    throw 'WAL file name must not contain a directory component.'
}

$pathRoot = [System.IO.Path]::GetPathRoot($ArchiveRoot)
if (-not [System.IO.Path]::IsPathRooted($ArchiveRoot) -or
    [string]::IsNullOrEmpty($pathRoot) -or $pathRoot.Length -lt 2 -or
    -not ($pathRoot.EndsWith('\') -or $pathRoot.EndsWith('/'))) {
    throw 'WAL archive root must be an absolute path.'
}
$root = Assert-ControlledPath $ArchiveRoot -AllowMissing

[System.IO.Directory]::CreateDirectory($root) | Out-Null
if (([System.IO.File]::GetAttributes($root) -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
    throw 'WAL archive root must not be a reparse point.'
}
$target = [System.IO.Path]::GetFullPath([System.IO.Path]::Combine($root, $WalFileName))
$prefix = $root.TrimEnd([char[]]@('\', '/')) + [System.IO.Path]::DirectorySeparatorChar
if (-not $target.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw 'WAL archive target escapes the configured archive root.'
}

function Test-SameFileContent([string]$Left, [string]$Right) {
    $leftInfo = [System.IO.FileInfo]::new($Left)
    $rightInfo = [System.IO.FileInfo]::new($Right)
    if ($leftInfo.Length -ne $rightInfo.Length) { return $false }
    $leftHash = Get-SharedFileHash $Left
    $rightHash = Get-SharedFileHash $Right
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
    $input = [System.IO.File]::Open($source, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, ([System.IO.FileShare]::ReadWrite -bor [System.IO.FileShare]::Delete))
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

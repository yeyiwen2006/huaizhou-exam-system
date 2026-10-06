param(
    [string]$SourcePath,
    [string]$WalFileName,
    [string]$ArchiveRoot
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'Postgresql.Operations.psm1') -Force -DisableNameChecking
try {
    if ([string]::IsNullOrWhiteSpace($SourcePath) -or
        [string]::IsNullOrWhiteSpace($WalFileName) -or
        [string]::IsNullOrWhiteSpace($ArchiveRoot)) {
        throw 'The WAL source, file name, and archive root are required.'
    }

    & (Join-Path $PSScriptRoot 'Invoke-WalArchive.ps1') `
        -SourcePath $SourcePath -WalFileName $WalFileName -ArchiveRoot $ArchiveRoot
    if ($LASTEXITCODE -ne 0) {
        throw 'The WAL archive copy did not complete.'
    }

    $source = [System.IO.Path]::GetFullPath($SourcePath)
    $target = [System.IO.Path]::GetFullPath([System.IO.Path]::Combine($ArchiveRoot, $WalFileName))
    if (-not [System.IO.File]::Exists($target) -or
        ([System.IO.File]::GetAttributes($target) -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'The WAL archive target is missing or is a reparse point.'
    }
    $sourceHash = Get-SharedFileHash $source
    $targetHash = Get-SharedFileHash $target
    if (-not [string]::Equals($sourceHash, $targetHash, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw 'The WAL archive target does not match the source.'
    }

    exit 0
}
catch {
    [Console]::Error.WriteLine("WAL archive command failed: $($_.Exception.Message)")
    exit 1
}

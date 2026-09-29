param(
    [Parameter(Mandatory = $true)][string]$DataDirectory,
    [Parameter(Mandatory = $true)][string]$ArchiveRoot,
    [Parameter(Mandatory = $true)][string]$BackupRoot,
    [Parameter(Mandatory = $true)][long]$MinimumFreeBytes
)

$ErrorActionPreference = 'Stop'
if ($MinimumFreeBytes -le 0) {
    throw 'MinimumFreeBytes must be positive and based on the measured WAL and backup workload.'
}

$errors = [System.Collections.Generic.List[string]]::new()
$volumes = [System.Collections.Generic.List[object]]::new()
foreach ($entry in @(
    @{ name = 'data'; path = $DataDirectory },
    @{ name = 'archive'; path = $ArchiveRoot },
    @{ name = 'backup'; path = $BackupRoot })) {
    $pathRoot = [System.IO.Path]::GetPathRoot($entry.path)
    if ([string]::IsNullOrWhiteSpace($pathRoot) -or
        $pathRoot -eq [string][System.IO.Path]::DirectorySeparatorChar -or
        -not $pathRoot.EndsWith([string][System.IO.Path]::DirectorySeparatorChar)) {
        $errors.Add("$($entry.name) path must be fully qualified.")
        continue
    }
    try {
        $path = [System.IO.Path]::GetFullPath($entry.path)
        if (-not [System.IO.Directory]::Exists($path)) {
            $errors.Add("$($entry.name) directory does not exist.")
            continue
        }
        $root = [System.IO.Path]::GetPathRoot($path)
        $directory = [System.IO.DirectoryInfo]::new($path)
        $hasReparsePoint = $false
        while ($null -ne $directory) {
            if (($directory.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                $hasReparsePoint = $true
                break
            }
            $directory = $directory.Parent
        }
        if ($hasReparsePoint) {
            $errors.Add("$($entry.name) path traverses a reparse point; inspect the actual volume explicitly.")
            continue
        }
        $drive = [System.IO.DriveInfo]::new($root)
        if (-not $drive.IsReady) {
            $errors.Add("$($entry.name) volume is not ready.")
            continue
        }
        $freeBytes = $drive.AvailableFreeSpace
        $volumes.Add([ordered]@{
            name = $entry.name
            volume = $root
            availableFreeBytes = $freeBytes
            minimumFreeBytes = $MinimumFreeBytes
        })
        if ($freeBytes -lt $MinimumFreeBytes) {
            $errors.Add("$($entry.name) volume has less free space than the required reserve.")
        }
    }
    catch {
        $errors.Add("$($entry.name) volume could not be inspected: $($_.Exception.Message)")
    }
}

[ordered]@{
    checkedAt = [DateTimeOffset]::UtcNow.ToString('O')
    ready = ($errors.Count -eq 0)
    errors = $errors
    volumes = $volumes
} | ConvertTo-Json -Depth 5
if ($errors.Count -ne 0) {
    throw "Storage capacity check failed with $($errors.Count) blocking condition(s)."
}

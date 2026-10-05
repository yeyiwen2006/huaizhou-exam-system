param(
    [Parameter(Mandatory = $true)][string]$DataDirectory,
    [Parameter(Mandatory = $true)][string]$ArchiveDirectory,
    [Parameter(Mandatory = $true)][string]$ResourceDirectory,
    [Parameter(Mandatory = $true)][string]$BackupDirectory,
    [Parameter(Mandatory = $true)][string]$ProtectedConfigurationDirectory,
    [ValidateRange(1, 9223372036854775807)][long]$MinimumAvailableBytes = 1073741824,
    [ValidateRange(1, 99)][int]$MinimumAvailablePercent = 10
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'Postgresql.Operations.psm1') -Force -DisableNameChecking
$errors = [Collections.Generic.List[string]]::new()
$volumes = [Collections.Generic.List[object]]::new()
foreach ($entry in ([ordered]@{ data = $DataDirectory; archive = $ArchiveDirectory; resources = $ResourceDirectory;
    backups = $BackupDirectory; protectedConfiguration = $ProtectedConfigurationDirectory }).GetEnumerator()) {
    try {
        $path = Assert-ControlledPath $entry.Value
        if (-not [IO.Directory]::Exists($path)) { throw 'An existing directory is required.' }
        # A mounted/junction ancestor is rejected rather than measuring the wrong parent volume.
        $volume = [IO.DriveInfo]::new([IO.Path]::GetPathRoot($path))
        $available = $volume.AvailableFreeSpace
        $total = $volume.TotalSize
        $percent = if ($total -gt 0) { 100.0 * $available / $total } else { 0 }
        $passed = $available -ge $MinimumAvailableBytes -and $percent -ge $MinimumAvailablePercent
        if (-not $passed) { $errors.Add("$($entry.Key): storage is below the configured absolute or percentage threshold.") }
        $volumes.Add([ordered]@{ purpose = $entry.Key; path = $path; volume = $volume.Name;
            availableBytes = $available; totalBytes = $total; availablePercent = [Math]::Round($percent, 2); passed = $passed })
    } catch { $errors.Add("$($entry.Key): directory/volume cannot be safely inspected.") }
}
[ordered]@{ checkedAt = [DateTimeOffset]::UtcNow.ToString('O'); ready = $errors.Count -eq 0;
    errors = $errors.ToArray(); volumes = $volumes.ToArray(); minimumAvailableBytes = $MinimumAvailableBytes;
    minimumAvailablePercent = $MinimumAvailablePercent } | ConvertTo-Json -Depth 6
if ($errors.Count -ne 0) { exit 1 }
exit 0

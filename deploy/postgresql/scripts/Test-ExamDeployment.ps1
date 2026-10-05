param(
    [Parameter(Mandatory = $true)][string]$ConfigurationPath,
    [ValidateRange(1, 2147483647)][int]$Samples = 1,
    [ValidateRange(5, 3600)][int]$IntervalSeconds = 10
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'Postgresql.Operations.psm1') -Force -DisableNameChecking
$configurationFile = Assert-ControlledPath $ConfigurationPath
if (-not [IO.File]::Exists($configurationFile) -or ([IO.FileInfo]::new($configurationFile)).Length -gt 65536) {
    throw 'Use an existing deployment-check JSON file no larger than 64 KiB.'
}
$configuration = [IO.File]::ReadAllText($configurationFile) | ConvertFrom-Json
$null = Assert-ReadinessConnection $configuration.primaryConnectionUri
$null = Assert-ReadinessConnection $configuration.standbyConnectionUri
$shell = @('pwsh.exe', 'powershell.exe', 'pwsh') | ForEach-Object { Join-Path $PSHOME $_ } | Where-Object { [IO.File]::Exists($_) } | Select-Object -First 1
if (-not $shell) { throw 'Cannot locate the current PowerShell runtime.' }

function Invoke-ReadCheck([string]$Script, [object[]]$Arguments) {
    $raw = & $shell -NoProfile -NonInteractive -File (Join-Path $PSScriptRoot $Script) @Arguments 2>$null | Out-String
    $code = $LASTEXITCODE
    try { $report = $raw | ConvertFrom-Json; if ($null -eq $report.ready) { throw 'No readiness value.' } }
    catch { $report = [ordered]@{ ready = $false; errors = @('Check could not produce a valid report. Inspect protected configuration and service-account permissions.') } }
    return [ordered]@{ exitCode = $code; report = $report }
}

$failed = $false
for ($index = 0; $index -lt $Samples; $index++) {
    $replication = Invoke-ReadCheck 'Test-ReplicationReadiness.ps1' @(
        '-PsqlPath', $configuration.psqlPath, '-PrimaryConnectionUri', $configuration.primaryConnectionUri,
        '-StandbyConnectionUri', $configuration.standbyConnectionUri, '-ExpectedSystemIdentifier', $configuration.expectedSystemIdentifier,
        '-StandbyApplicationName', $configuration.standbyApplicationName, '-ReplicationSlot', $configuration.replicationSlot,
        '-MaximumSlotWalBytes', $configuration.maximumSlotWalBytes)
    $storage = Invoke-ReadCheck 'Test-StorageCapacity.ps1' @(
        '-DataDirectory', $configuration.storage.dataDirectory, '-ArchiveDirectory', $configuration.storage.archiveDirectory,
        '-ResourceDirectory', $configuration.storage.resourceDirectory, '-BackupDirectory', $configuration.storage.backupDirectory,
        '-ProtectedConfigurationDirectory', $configuration.storage.protectedConfigurationDirectory,
        '-MinimumAvailableBytes', $configuration.storage.minimumAvailableBytes, '-MinimumAvailablePercent', $configuration.storage.minimumAvailablePercent)
    $ready = $replication.exitCode -eq 0 -and $replication.report.ready -eq $true -and
        $storage.exitCode -eq 0 -and $storage.report.ready -eq $true
    $failed = $failed -or -not $ready
    [ordered]@{ checkedAt = [DateTimeOffset]::UtcNow.ToString('O'); sample = $index + 1; ready = $ready;
        replication = $replication; storage = $storage } | ConvertTo-Json -Depth 12 -Compress
    if ($index + 1 -lt $Samples) { Start-Sleep -Seconds $IntervalSeconds }
}
if ($failed) { exit 1 }
exit 0

param(
    [Parameter(Mandatory = $true)][string]$PostgresBinDirectory,
    [Parameter(Mandatory = $true)][string]$StandbyDataDirectory,
    [Parameter(Mandatory = $true)][string]$PrimaryHost,
    [int]$PrimaryPort = 5432,
    [Parameter(Mandatory = $true)][string]$ReplicationUser,
    [ValidatePattern('^[a-z0-9_]+$')][string]$ReplicationSlot = 'exam_standby'
)

$ErrorActionPreference = 'Stop'
$bin = [System.IO.Path]::GetFullPath($PostgresBinDirectory)
$target = [System.IO.Path]::GetFullPath($StandbyDataDirectory)
$tool = [System.IO.Path]::Combine($bin, 'pg_basebackup.exe')
$psql = [System.IO.Path]::Combine($bin, 'psql.exe')
if (-not [System.IO.File]::Exists($tool)) {
    throw "pg_basebackup.exe not found: $tool"
}
if (-not [System.IO.File]::Exists($psql)) {
    throw "psql.exe not found: $psql"
}

if ([System.IO.Directory]::Exists($target) -and
    [System.IO.Directory]::EnumerateFileSystemEntries($target).GetEnumerator().MoveNext()) {
    throw 'The standby data directory must be absent or empty. Existing data will not be overwritten.'
}

[System.IO.Directory]::CreateDirectory($target) | Out-Null

function Get-ReplicationSlotState {
    $sql = "SELECT COALESCE((SELECT CASE WHEN active THEN 'active' ELSE 'inactive' END FROM pg_replication_slots WHERE slot_name = '$ReplicationSlot'), 'missing');"
    $value = & $psql --host $PrimaryHost --port $PrimaryPort --username $ReplicationUser `
        --dbname postgres --no-password --no-psqlrc --set ON_ERROR_STOP=1 `
        --tuples-only --no-align --command $sql
    if ($LASTEXITCODE -ne 0) { throw 'Could not inspect the replication slot on the primary.' }
    return ([string]$value).Trim()
}

if ((Get-ReplicationSlotState) -ne 'missing') {
    throw "Replication slot '$ReplicationSlot' already exists; it will not be reused or removed."
}

$backupExitCode = 0
try {
    & $tool --host $PrimaryHost --port $PrimaryPort --username $ReplicationUser `
        --pgdata $target --write-recovery-conf --wal-method stream --checkpoint fast `
        --slot $ReplicationSlot --create-slot --no-password
    $backupExitCode = $LASTEXITCODE
}
catch {
    $backupExitCode = if ($LASTEXITCODE) { $LASTEXITCODE } else { 1 }
}

if ($backupExitCode -ne 0) {
    try {
        if ((Get-ReplicationSlotState) -eq 'inactive') {
            $dropSql = "SELECT pg_drop_replication_slot('$ReplicationSlot');"
            & $psql --host $PrimaryHost --port $PrimaryPort --username $ReplicationUser `
                --dbname postgres --no-password --no-psqlrc --set ON_ERROR_STOP=1 `
                --tuples-only --no-align --command $dropSql | Out-Null
            if ($LASTEXITCODE -ne 0) {
                Write-Warning "Failed to remove the inactive replication slot '$ReplicationSlot'."
            }
        }
    }
    catch {
        Write-Warning "Could not verify or remove the slot created by the failed base backup: $($_.Exception.Message)"
    }
    throw "pg_basebackup failed with exit code $backupExitCode. Partial data was preserved at '$target'."
}

param(
    [Parameter(Mandatory = $true)][string]$PostgresBinDirectory,
    [Parameter(Mandatory = $true)][string]$StandbyDataDirectory,
    [Parameter(Mandatory = $true)][string]$PrimaryHost,
    [Parameter(Mandatory = $true)][ValidatePattern('^[0-9]{1,20}$')][string]$ExpectedSystemIdentifier,
    [ValidateRange(1, 65535)][int]$PrimaryPort = 5432,
    [Parameter(Mandatory = $true)][ValidatePattern('^[a-z_][a-z0-9_]*$')][string]$ReplicationUser,
    [ValidatePattern('^[a-z0-9_]+$')][string]$ReplicationSlot = 'exam_standby',
    [ValidatePattern('^[a-zA-Z_][a-zA-Z0-9_.-]*$')][string]$StandbyApplicationName = 'exam_standby'
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'Postgresql.Operations.psm1') -Force -DisableNameChecking
$bin = Assert-ControlledPath $PostgresBinDirectory
$target = Assert-ControlledPath $StandbyDataDirectory -AllowMissing
if ($target.TrimEnd('\', '/') -eq [IO.Path]::GetPathRoot($target).TrimEnd('\', '/')) { throw 'A volume root cannot be a standby data directory.' }
$tool = [System.IO.Path]::Combine($bin, 'pg_basebackup.exe')
$psql = [System.IO.Path]::Combine($bin, 'psql.exe')
if (-not [System.IO.File]::Exists($tool)) {
    throw "pg_basebackup.exe not found: $tool"
}
if (-not [System.IO.File]::Exists($psql)) {
    throw "psql.exe not found: $psql"
}
if (-not [Net.IPAddress]::TryParse($PrimaryHost, [ref]([Net.IPAddress]$null))) { throw 'PrimaryHost must be a deployment IP address.' }
if ($env:PGSSLMODE -ne 'verify-full' -or -not $env:PGSSLROOTCERT) { throw 'Set protected PGSSLMODE=verify-full and PGSSLROOTCERT before initializing.' }
$null = Assert-ControlledPath $env:PGSSLROOTCERT
$backupVersion = (& $tool --version) -join ''
if ($LASTEXITCODE -ne 0 -or $backupVersion -notmatch '\(PostgreSQL\) (18\.\d+)') { throw 'PostgreSQL 18 pg_basebackup is required.' }
$expectedVersion = $Matches[1]
$psqlVersion = (& $psql --version) -join ''
if ($LASTEXITCODE -ne 0 -or $psqlVersion -notmatch '\(PostgreSQL\) (18\.\d+)' -or $Matches[1] -ne $expectedVersion) { throw 'psql and pg_basebackup must use the same PostgreSQL 18 patch version.' }

if ([System.IO.Directory]::Exists($target) -and
    [System.IO.Directory]::EnumerateFileSystemEntries($target).GetEnumerator().MoveNext()) {
    throw 'The standby data directory must be absent or empty. Existing data will not be overwritten.'
}

function Get-PrimaryInitializationState {
    $sql = "SELECT json_build_object('systemIdentifier', (SELECT system_identifier::text FROM pg_control_system()), 'inRecovery', pg_is_in_recovery(), 'versionNumber', current_setting('server_version_num')::int, 'externalTablespaces', (SELECT count(*) FROM pg_tablespace WHERE spcname NOT IN ('pg_default', 'pg_global')), 'slotState', COALESCE((SELECT CASE WHEN active THEN 'active' ELSE 'inactive' END FROM pg_replication_slots WHERE slot_name = '$ReplicationSlot'), 'missing'));"
    $value = & $psql --host $PrimaryHost --port $PrimaryPort --username $ReplicationUser `
        --dbname postgres --no-password --no-psqlrc --set ON_ERROR_STOP=1 `
        --tuples-only --no-align --command $sql
    if ($LASTEXITCODE -ne 0) { throw 'Could not inspect the replication slot on the primary.' }
    return ($value | ConvertFrom-Json)
}

$primaryState = Get-PrimaryInitializationState
$expectedServerVersion = 180000 + [int]$expectedVersion.Split('.')[1]
if ($primaryState.systemIdentifier -ne $ExpectedSystemIdentifier -or $primaryState.inRecovery -ne $false -or
    $primaryState.versionNumber -ne $expectedServerVersion) { throw 'Primary cluster identity, role or PostgreSQL patch version does not match deployment.' }
# Plain backups otherwise materialize external tablespaces at source absolute paths.
# This initializer supports only a self-contained data directory, not arbitrary tablespace remapping.
if ($null -eq $primaryState.externalTablespaces -or [long]$primaryState.externalTablespaces -ne 0) {
    throw 'External tablespaces are unsupported by this controlled initializer; use a reviewed explicit mapping workflow.'
}
if ($primaryState.slotState -ne 'missing') {
    throw "Replication slot '$ReplicationSlot' already exists; it will not be reused or removed."
}
[System.IO.Directory]::CreateDirectory($target) | Out-Null

$backupExitCode = 0
$previousApplicationName = $env:PGAPPNAME
try {
    # pg_basebackup -R writes postgresql.auto.conf, which overrides a config fragment.
    $env:PGAPPNAME = $StandbyApplicationName
    & $tool --host $PrimaryHost --port $PrimaryPort --username $ReplicationUser `
        --pgdata $target --write-recovery-conf --wal-method stream --checkpoint fast --compress=server-gzip:level=1 `
        --slot $ReplicationSlot --create-slot --no-password --no-clean
    $backupExitCode = $LASTEXITCODE
}
catch {
    $backupExitCode = if ($LASTEXITCODE) { $LASTEXITCODE } else { 1 }
}
finally { $env:PGAPPNAME = $previousApplicationName }

if ($backupExitCode -ne 0) {
    # A missing-slot precheck does not prove ownership if another initializer races us.
    throw "pg_basebackup failed with exit code $backupExitCode. Partial data and replication slots are preserved. Inspect ownership before retrying in a new empty directory."
}

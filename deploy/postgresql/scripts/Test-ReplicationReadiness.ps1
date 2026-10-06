param(
    [Parameter(Mandatory = $true)][string]$PsqlPath,
    [Parameter(Mandatory = $true)][string]$PrimaryConnectionUri,
    [Parameter(Mandatory = $true)][string]$StandbyConnectionUri,
    [Parameter(Mandatory = $true)][ValidatePattern('^[0-9]{1,20}$')][string]$ExpectedSystemIdentifier,
    [ValidatePattern('^[a-zA-Z_][a-zA-Z0-9_.-]*$')][string]$StandbyApplicationName = 'exam_standby',
    [ValidatePattern('^[a-z0-9_]+$')][string]$ReplicationSlot = 'exam_standby',
    [ValidateRange(1, 9223372036854775807)][long]$MaximumSlotWalBytes = 8589934592
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
Import-Module (Join-Path $PSScriptRoot 'Postgresql.Operations.psm1') -Force -DisableNameChecking
$psql = Assert-ControlledPath $PsqlPath
foreach ($connection in @($PrimaryConnectionUri, $StandbyConnectionUri)) {
    $null = Assert-ReadinessConnection $connection
}
if (-not [System.IO.File]::Exists($psql)) {
    throw "psql.exe not found: $psql"
}

$primarySql = @'
SELECT json_build_object(
  'systemIdentifier', (SELECT system_identifier::text FROM pg_control_system()),
  'database', current_database(),
  'versionNumber', current_setting('server_version_num')::int,
  'timeline', (SELECT timeline_id FROM pg_control_checkpoint()),
  'inRecovery', pg_is_in_recovery(),
  'fsync', current_setting('fsync'),
  'fullPageWrites', current_setting('full_page_writes'),
  'flushLsn', pg_current_wal_flush_lsn(),
  'maxSlotWalKeepBytes', pg_size_bytes(current_setting('max_slot_wal_keep_size')),
  'archiveMode', current_setting('archive_mode'),
  'archiveCount', (SELECT archived_count FROM pg_stat_archiver),
  'lastArchiveSuccess', (SELECT last_archived_time FROM pg_stat_archiver),
  'lastArchiveFailure', (SELECT last_failed_time FROM pg_stat_archiver),
  'slots', COALESCE((SELECT json_agg(json_build_object(
      'active', active, 'walStatus', wal_status, 'safeWalBytes', safe_wal_size,
      'retainedBytes', pg_wal_lsn_diff(pg_current_wal_lsn(), restart_lsn)))
      FROM pg_replication_slots WHERE slot_name = '__REQUIRED_SLOT__' AND slot_type = 'physical'), '[]'::json),
  'synchronousCommit', current_setting('synchronous_commit'),
  'synchronousStandbyNames', current_setting('synchronous_standby_names'),
  'standbys', COALESCE((SELECT json_agg(json_build_object(
      'applicationName', application_name,
      'state', state,
      'syncState', sync_state,
      'writeLsn', write_lsn,
      'flushLsn', flush_lsn,
      'replayLsn', replay_lsn)) FROM pg_stat_replication), '[]'::json));
'@
$standbySql = @'
SELECT json_build_object(
  'systemIdentifier', (SELECT system_identifier::text FROM pg_control_system()),
  'database', current_database(),
  'versionNumber', current_setting('server_version_num')::int,
  'receiver', (SELECT json_build_object('status', status, 'timeline', received_tli,
      'senderHost', sender_host, 'senderPort', sender_port, 'slot', slot_name) FROM pg_stat_wal_receiver),
  'inRecovery', pg_is_in_recovery(),
  'fsync', current_setting('fsync'),
  'fullPageWrites', current_setting('full_page_writes'),
  'receiveLsn', pg_last_wal_receive_lsn(),
  'replayLsn', pg_last_wal_replay_lsn(),
  'replayPaused', pg_is_wal_replay_paused());
'@

$primarySql = $primarySql.Replace('__REQUIRED_SLOT__', $ReplicationSlot)
$previousTimeout = $env:PGCONNECT_TIMEOUT
$previousOptions = $env:PGOPTIONS
try {
    $env:PGCONNECT_TIMEOUT = '5'
    $env:PGOPTIONS = '-c statement_timeout=5000 -c default_transaction_read_only=on'
    $primaryText = & $psql --dbname $PrimaryConnectionUri --no-password --no-psqlrc --set ON_ERROR_STOP=1 --tuples-only --no-align --command $primarySql
    if ($LASTEXITCODE -ne 0) { throw 'Primary readiness query failed.' }
    $standbyText = & $psql --dbname $StandbyConnectionUri --no-password --no-psqlrc --set ON_ERROR_STOP=1 --tuples-only --no-align --command $standbySql
    if ($LASTEXITCODE -ne 0) { throw 'Standby readiness query failed.' }
} finally {
    $env:PGCONNECT_TIMEOUT = $previousTimeout
    $env:PGOPTIONS = $previousOptions
}

$primary = $primaryText | ConvertFrom-Json
$standby = $standbyText | ConvertFrom-Json
$primary | Add-Member -NotePropertyName expectedSystemIdentifier -NotePropertyValue $ExpectedSystemIdentifier
$primaryUri = [Uri]$PrimaryConnectionUri
$primary | Add-Member -NotePropertyName expectedHost -NotePropertyValue ([Net.IPAddress]::Parse($primaryUri.Host.Trim('[', ']')).ToString())
$primary | Add-Member -NotePropertyName expectedPort -NotePropertyValue $(if ($primaryUri.Port -gt 0) { $primaryUri.Port } else { 5432 })
$primary | Add-Member -NotePropertyName expectedSlot -NotePropertyValue $ReplicationSlot
$errors = Get-ReplicationErrors $primary $standby $StandbyApplicationName $MaximumSlotWalBytes

$report = [ordered]@{
    checkedAt = [DateTimeOffset]::UtcNow.ToString('O')
    ready = ($errors.Count -eq 0)
    errors = $errors
    primary = $primary
    standby = $standby
}
$report | ConvertTo-Json -Depth 8
if ($errors.Length -ne 0) {
    throw "Replication readiness failed with $($errors.Count) blocking condition(s)."
}

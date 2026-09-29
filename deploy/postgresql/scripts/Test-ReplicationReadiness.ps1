param(
    [Parameter(Mandatory = $true)][string]$PsqlPath,
    [Parameter(Mandatory = $true)][string]$PrimaryConnectionUri,
    [Parameter(Mandatory = $true)][string]$StandbyConnectionUri,
    [string]$StandbyApplicationName = 'exam_standby',
    [ValidatePattern('^[a-z0-9_]+$')][string]$ReplicationSlot = 'exam_standby',
    [Parameter(Mandatory = $true)][long]$MinimumSafeWalBytes
)

$ErrorActionPreference = 'Stop'
if ($MinimumSafeWalBytes -le 0) {
    throw 'MinimumSafeWalBytes must be a positive capacity reserve.'
}
$psql = [System.IO.Path]::GetFullPath($PsqlPath)
if (-not [System.IO.File]::Exists($psql)) {
    throw "psql.exe not found: $psql"
}

$primarySql = @'
SELECT json_build_object(
  'inRecovery', pg_is_in_recovery(),
  'synchronousCommit', current_setting('synchronous_commit'),
  'synchronousStandbyNames', current_setting('synchronous_standby_names'),
  'maxSlotWalKeepSizeMb', (SELECT setting::bigint FROM pg_settings WHERE name = 'max_slot_wal_keep_size'),
  'slots', COALESCE((SELECT json_agg(json_build_object(
      'slotName', slot_name,
      'slotType', slot_type,
      'active', active,
      'walStatus', wal_status,
      'safeWalSize', safe_wal_size)) FROM pg_replication_slots), '[]'::json),
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
  'inRecovery', pg_is_in_recovery(),
  'receiveLsn', pg_last_wal_receive_lsn(),
  'replayLsn', pg_last_wal_replay_lsn(),
  'replayPaused', pg_is_wal_replay_paused());
'@

$primaryText = & $psql --dbname $PrimaryConnectionUri --no-psqlrc --set ON_ERROR_STOP=1 --tuples-only --no-align --command $primarySql
if ($LASTEXITCODE -ne 0) { throw 'Primary readiness query failed.' }
$standbyText = & $psql --dbname $StandbyConnectionUri --no-psqlrc --set ON_ERROR_STOP=1 --tuples-only --no-align --command $standbySql
if ($LASTEXITCODE -ne 0) { throw 'Standby readiness query failed.' }

$primary = $primaryText | ConvertFrom-Json
$standby = $standbyText | ConvertFrom-Json
$errors = [System.Collections.Generic.List[string]]::new()
if ($primary.inRecovery) { $errors.Add('The configured primary is in recovery.') }
if ($primary.synchronousCommit -ne 'remote_apply') {
    $errors.Add("Primary synchronous_commit is '$($primary.synchronousCommit)', not 'remote_apply'.")
}
if ([string]::IsNullOrWhiteSpace([string]$primary.synchronousStandbyNames) -or
    -not ([string]$primary.synchronousStandbyNames).Contains($StandbyApplicationName)) {
    $errors.Add('Primary synchronous_standby_names does not name the required standby.')
}

$matchingStandbys = @($primary.standbys) | Where-Object {
    $_.applicationName -eq $StandbyApplicationName -and
    $_.state -eq 'streaming' -and
    $_.syncState -eq 'sync' -and
    -not [string]::IsNullOrWhiteSpace([string]$_.replayLsn)
}
if ($matchingStandbys.Count -ne 1) {
    $errors.Add('Primary does not report exactly one streaming synchronous standby with a replay LSN.')
}
if ($null -eq $primary.maxSlotWalKeepSizeMb -or [long]$primary.maxSlotWalKeepSizeMb -le 0) {
    $errors.Add('Primary max_slot_wal_keep_size must be a positive finite value.')
}
$matchingSlots = @($primary.slots) | Where-Object { $_.slotName -eq $ReplicationSlot }
if ($matchingSlots.Count -ne 1 -or $matchingSlots[0].slotType -ne 'physical' -or
    -not $matchingSlots[0].active) {
    $errors.Add('Primary does not report one active physical replication slot for the standby.')
}
elseif ($matchingSlots[0].walStatus -notin @('reserved', 'extended') -or
    $null -eq $matchingSlots[0].safeWalSize -or [long]$matchingSlots[0].safeWalSize -le $MinimumSafeWalBytes) {
    $errors.Add('Standby replication slot has lost WAL or has insufficient safe WAL headroom.')
}
if (-not $standby.inRecovery) { $errors.Add('The configured standby is not in recovery.') }
if ($standby.replayPaused) { $errors.Add('Standby WAL replay is paused.') }
if ([string]::IsNullOrWhiteSpace([string]$standby.receiveLsn) -or
    [string]::IsNullOrWhiteSpace([string]$standby.replayLsn) -or
    $standby.receiveLsn -ne $standby.replayLsn) {
    $errors.Add('Standby has not replayed all WAL it has received.')
}

$report = [ordered]@{
    checkedAt = [DateTimeOffset]::UtcNow.ToString('O')
    ready = ($errors.Count -eq 0)
    errors = $errors
    primary = $primary
    standby = $standby
}
$report | ConvertTo-Json -Depth 8
if ($errors.Count -ne 0) {
    throw "Replication readiness failed with $($errors.Count) blocking condition(s)."
}

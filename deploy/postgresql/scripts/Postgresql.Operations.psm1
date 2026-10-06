Set-StrictMode -Version Latest

function Assert-ControlledPath([string]$Path, [switch]$AllowMissing) {
    $root = [IO.Path]::GetPathRoot($Path)
    if (-not [IO.Path]::IsPathRooted($Path) -or
        ($root -ne '/' -and ($root.Length -lt 3 -or -not $root.EndsWith('\'))) -or
        $Path.StartsWith('\\') -or $Path -match "['`"%\r\n]") {
        throw 'Use an absolute local path without shell/configuration delimiters.'
    }
    if ($Path.Substring($root.Length).Contains(':')) { throw 'Alternate data stream paths are not permitted.' }
    $full = [IO.Path]::GetFullPath($Path)
    $cursor = $full
    while ($cursor) {
        if (([IO.File]::Exists($cursor) -or [IO.Directory]::Exists($cursor)) -and
            (([IO.File]::GetAttributes($cursor) -band [IO.FileAttributes]::ReparsePoint) -ne 0)) {
            throw 'A configured path or ancestor is a reparse point.'
        }
        $parent = [IO.Path]::GetDirectoryName($cursor)
        if ($parent -eq $cursor) { break }
        $cursor = $parent
    }
    if (-not $AllowMissing -and -not ([IO.Directory]::Exists($full) -or [IO.File]::Exists($full))) {
        throw 'A configured path is missing; it will not be created automatically.'
    }
    return $full
}

function ConvertFrom-PgLsn([string]$Value) {
    if ($Value -notmatch '^([0-9a-fA-F]{1,8})/([0-9a-fA-F]{1,8})$') { throw 'Invalid or missing WAL position.' }
    return [decimal][Convert]::ToUInt32($Matches[1], 16) * 4294967296 + [Convert]::ToUInt32($Matches[2], 16)
}

function Assert-ReadinessConnection([string]$Value) {
    $uri = [Uri]$Value
    if (-not $uri.IsAbsoluteUri -or $uri.Scheme -notin @('postgres', 'postgresql') -or
        [Uri]::UnescapeDataString($uri.UserInfo).Contains(':') -or $uri.Fragment -or
        [Uri]::UnescapeDataString($uri.Query) -match '(?i)(password|passfile|options)=' -or
        $uri.Query -cne '?sslmode=verify-full') {
        throw 'Use a PostgreSQL URI without passwords or injected options and with sslmode=verify-full; use protected pgpass.'
    }
    # libpq accepts repeated/overriding query parameters. Only the fixed TLS setting is
    # permitted here; CA and password files come from the protected process environment.
    # Otherwise host/hostaddr/port/service could invalidate the endpoint inspected below.
    $null = [Net.IPAddress]::Parse($uri.Host.Trim('[', ']'))
    return $uri
}

function Get-ReplicationErrors($Primary, $Standby, [string]$ApplicationName, [long]$MaximumSlotWalBytes) {
    $errors = [Collections.Generic.List[string]]::new()
    try {
        if ($ApplicationName -notmatch '^[a-zA-Z_][a-zA-Z0-9_.-]*$') { throw 'Invalid standby application name.' }
        if ($MaximumSlotWalBytes -le 0) { throw 'Slot budget must be finite and positive.' }
        if ($Primary.inRecovery -ne $false -or $Standby.inRecovery -ne $true) { $errors.Add('Primary/standby recovery roles are incorrect.') }
        if ([string]::IsNullOrWhiteSpace($Primary.systemIdentifier) -or $Primary.systemIdentifier -ne $Standby.systemIdentifier -or
            $Primary.systemIdentifier -ne $Primary.expectedSystemIdentifier -or
            $Primary.database -ne $Standby.database) { $errors.Add('Cluster identifier or database differs.') }
        if ([int]$Primary.versionNumber -lt 180000 -or [int]$Primary.versionNumber -ge 190000 -or
            $Primary.versionNumber -ne $Standby.versionNumber) { $errors.Add('Both nodes must use the same PostgreSQL 18 patch version.') }
        if ($Primary.synchronousCommit -ne 'remote_apply' -or $Primary.fsync -ne 'on' -or $Primary.fullPageWrites -ne 'on' -or
            $Standby.fsync -ne 'on' -or $Standby.fullPageWrites -ne 'on') {
            $errors.Add('Synchronous replay and local WAL durability settings are required.')
        }
        $escaped = [regex]::Escape($ApplicationName)
        $single = '(?:' + $escaped + '|"' + $escaped + '")'
        if ([string]$Primary.synchronousStandbyNames -notmatch ('^\s*(?:' + $single + '|(?:FIRST|ANY)\s+1\s*\(\s*' + $single + '\s*\))\s*$')) {
            $errors.Add('Synchronous standby configuration must name exactly the required peer.')
        }
        $matching = @($Primary.standbys | Where-Object { $_.applicationName -ceq $ApplicationName })
        if ($matching.Count -ne 1 -or $matching[0].state -ne 'streaming' -or $matching[0].syncState -notin @('sync', 'quorum')) {
            $errors.Add('Exactly one streaming synchronous required peer must be connected.')
        } else {
            if ((ConvertFrom-PgLsn $matching[0].replayLsn) -lt (ConvertFrom-PgLsn $Primary.flushLsn)) {
                $errors.Add('Peer replay is behind the sampled primary flush boundary.')
            }
        }
        if ($Standby.replayPaused -ne $false -or
            (ConvertFrom-PgLsn $Standby.receiveLsn) -ne (ConvertFrom-PgLsn $Standby.replayLsn) -or
            (ConvertFrom-PgLsn $Standby.replayLsn) -lt (ConvertFrom-PgLsn $Primary.flushLsn)) {
            $errors.Add('Standby is paused or has not replayed the sampled WAL boundary.')
        }
        if ($null -eq $Standby.receiver -or $Standby.receiver.status -ne 'streaming' -or
            $Standby.receiver.timeline -ne $Primary.timeline -or $Standby.receiver.senderHost -ne $Primary.expectedHost -or
            $Standby.receiver.senderPort -ne $Primary.expectedPort -or $Standby.receiver.slot -ne $Primary.expectedSlot) {
            $errors.Add('Standby receiver is not following the expected primary endpoint, slot and timeline.')
        }
        if ($Primary.maxSlotWalKeepBytes -le 0 -or $Primary.maxSlotWalKeepBytes -gt $MaximumSlotWalBytes) {
            $errors.Add('Replication slot retention is unlimited or exceeds the configured operator budget.')
        }
        if (@($Primary.slots).Count -ne 1) { $errors.Add('Exactly one configured physical replication slot is required.') }
        foreach ($slot in $Primary.slots) {
            if ($slot.active -ne $true -or $slot.walStatus -notin @('reserved', 'extended') -or
                $null -eq $slot.retainedBytes -or $slot.retainedBytes -ge $MaximumSlotWalBytes -or
                $null -eq $slot.safeWalBytes -or $slot.safeWalBytes -le 0) {
                $errors.Add('Replication slot is inactive, invalidated or approaching/exceeding its WAL budget; rebuild may be required.')
            }
        }
        if ($Primary.archiveMode -ne 'on' -or $Primary.archiveCount -lt 1 -or
            $null -ne $Primary.lastArchiveFailure -and
                ($null -eq $Primary.lastArchiveSuccess -or [DateTimeOffset]$Primary.lastArchiveFailure -gt [DateTimeOffset]$Primary.lastArchiveSuccess)) {
            $errors.Add('WAL archive has no successful receipt or has an unresolved failure.')
        }
    } catch { $errors.Add('Readiness data is incomplete or invalid; readiness cannot be established.') }
    return ,$errors.ToArray()
}

# PostgreSQL keeps completed WAL segments open with read/write handles on Windows.
# Share those handles; archive only on its callback and still verify the copied bytes.
function Get-SharedFileHash([string]$Path) {
    $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete)
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return [Convert]::ToBase64String($sha.ComputeHash($stream)) }
    finally { $sha.Dispose(); $stream.Dispose() }
}

Export-ModuleMember -Function Get-SharedFileHash, Assert-ControlledPath, Assert-ReadinessConnection, ConvertFrom-PgLsn, Get-ReplicationErrors

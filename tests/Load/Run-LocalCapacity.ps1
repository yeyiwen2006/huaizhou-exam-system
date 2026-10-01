param(
    [Parameter(Mandatory)][string]$FixtureDirectory,
    [Parameter(Mandatory)][string]$ReportDirectory,
    [ValidateRange(1, 3600)][int]$SteadySeconds = 120,
    [ValidateSet('mixed', 'separate')][string]$RunMode = 'mixed',
    [ValidateRange(1024, 65535)][int]$Port = 5089
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$fixture = (Resolve-Path $FixtureDirectory).Path
$report = [IO.Path]::GetFullPath($ReportDirectory)
if (Test-Path -LiteralPath $report) { throw 'ReportDirectory must be new.' }
$configuration = Get-Content -LiteralPath (Join-Path $fixture 'local-server.json') -Raw -Encoding UTF8 | ConvertFrom-Json -AsHashtable
$manifest = Get-Content -LiteralPath (Join-Path $fixture 'sessions.json') -Raw -Encoding UTF8 | ConvertFrom-Json -AsHashtable
$setup = Get-Content -LiteralPath (Join-Path $fixture 'setup.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if (!$manifest.synthetic -or !$setup.synthetic -or $setup.database -notmatch '^exam_load_[a-f0-9]{32}$') { throw 'Only isolated synthetic fixtures are accepted.' }
$setupDll = Join-Path $repo 'tests/Load/Exam.LoadSetup/bin/Release/net10.0/Exam.LoadSetup.dll'
& dotnet $setupDll --validate-fixture $fixture
if ($LASTEXITCODE -ne 0) { throw 'Isolated fixture validation failed; no server was started.' }
$serverDll = Join-Path $repo 'src/Exam.Server/bin/Release/net10.0/Exam.Server.dll'
$loadDll = Join-Path $repo 'tests/Load/Exam.LoadTests/bin/Release/net10.0/Exam.LoadTests.dll'
if (!(Test-Path $serverDll) -or !(Test-Path $loadDll)) { throw 'Build the Release solution first.' }
New-Item -ItemType Directory -Path $report | Out-Null
$serverEnvironment = @{ ASPNETCORE_ENVIRONMENT = 'Development'; Database__ApplyMigrations = 'true'; Logging__LogLevel__Default = 'Warning' }
if (Get-ChildItem Env: | Where-Object Name -match '^Kestrel__Endpoints__') { throw 'Remove inherited Kestrel endpoint overrides before local testing.' }
if ([Net.NetworkInformation.IPGlobalProperties]::GetIPGlobalProperties().GetActiveTcpListeners().Port -contains $Port) { throw 'The test port is already in use.' }
function Add-Settings([hashtable]$Settings, [string]$Prefix = '') {
    foreach ($entry in $Settings.GetEnumerator()) {
        $name = if ($Prefix) { $Prefix + '__' + $entry.Key } else { $entry.Key }
        if ($entry.Value -is [hashtable]) { Add-Settings $entry.Value $name }
        elseif ($entry.Value -is [array]) {
            for ($i = 0; $i -lt $entry.Value.Count; $i++) { $serverEnvironment[$name + '__' + $i] = [string]$entry.Value[$i] }
        } else { $serverEnvironment[$name] = [string]$entry.Value }
    }
}
Add-Settings $configuration
$serverEnvironment['Database__ApplyMigrations'] = 'true'
$target = "http://127.0.0.1:$Port"
$process = Start-Process -FilePath (Get-Command dotnet).Source -ArgumentList @('exec', ('"' + $serverDll + '"'), '--urls', $target) -Environment $serverEnvironment -WorkingDirectory $repo -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $report 'server.stdout.log') -RedirectStandardError (Join-Path $report 'server.stderr.log')
$results = [Collections.Generic.List[object]]::new()
$clock = [Diagnostics.Stopwatch]::StartNew()
$version = 0
try {
    $ready = $false
    for ($attempt = 0; $attempt -lt 60; $attempt++) {
        if ($process.HasExited) { throw 'Isolated server exited; inspect its local log.' }
        try {
            $health = Invoke-RestMethod "$target/health/ready" -TimeoutSec 2
            $ready = $health.status -eq 'ready' -and $health.serverId -eq $manifest.serverId -and $health.nodeId -eq $configuration.ExamServer.NodeId -and $health.serverEpoch -eq $manifest.serverEpoch
        } catch { }
        if ($ready) { break }
        Start-Sleep -Milliseconds 500
    }
    if (!$ready) { throw 'Isolated server did not become ready.' }
    $allSessions = $manifest.sessions
    $diagnosticHeaders = @{ Authorization = 'Bearer ' + $manifest.operatorToken }
    function Invoke-Scenario([string]$Scenario, [int]$Count, [string]$Label) {
        $script:version += 10000
        $manifest.sessions = @($allSessions | Select-Object -First $Count)
        foreach ($session in $manifest.sessions) { $session.initialAnswerVersion = $script:version }
        $sessionPath = Join-Path $report ($Label + '.sessions.json')
        # Contains synthetic credentials: keep this entire run directory private/ignored.
        $manifest | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $sessionPath -Encoding UTF8
        $arguments = @(('"' + $loadDll + '"'), '--target', $target, '--sessions', ('"' + $sessionPath + '"'), '--scenario', $Scenario, '--output', ('"' + (Join-Path $report $Label) + '"'), '--confirm-synthetic', '--allow-loopback-http', '--duration-seconds', $SteadySeconds)
        $loadProcess = Start-Process -FilePath (Get-Command dotnet).Source -ArgumentList $arguments -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $report ($Label + '.stdout.log')) -RedirectStandardError (Join-Path $report ($Label + '.stderr.log'))
        $observations = [Collections.Generic.List[object]]::new()
        try {
            while (!$loadProcess.HasExited) {
                try {
                    $snapshot = Invoke-RestMethod "$target/api/v1/diagnostics/runtime" -Headers $diagnosticHeaders -TimeoutSec 3
                    $observations.Add($snapshot.result)
                } catch { $observations.Add([pscustomobject]@{ sampledAt = [DateTimeOffset]::UtcNow; unavailable = $true }) }
                if (!$loadProcess.HasExited) { Start-Sleep -Seconds 2 }
            }
            $loadProcess.WaitForExit()
            $code = $loadProcess.ExitCode
        } finally {
            if (!$loadProcess.HasExited) { Stop-Process -InputObject $loadProcess; $loadProcess.WaitForExit() }
            $observations.ToArray() | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath (Join-Path $report ($Label + '.observations.json')) -Encoding UTF8
        }
        Get-Content -LiteralPath (Join-Path $report ($Label + '.stdout.log'))
        $process.Refresh()
        $results.Add([pscustomobject]@{ scenario = $Scenario; count = $Count; label = $Label; exitCode = $code; serverPeakWorkingSetBytes = $process.PeakWorkingSet64; serverCpuSeconds = $process.TotalProcessorTime.TotalSeconds })
        if ($code -notin @(0, 1)) { throw "Scenario $Label failed to run (exit $code)." }
    }
    foreach ($count in @(10, 50, 100, 200, 300)) {
        if ($count -le $allSessions.Count) { Invoke-Scenario burst $count "burst-$count" }
    }
    if ($RunMode -eq 'mixed') { Invoke-Scenario mixed $allSessions.Count 'mixed' }
    else {
        Invoke-Scenario steady $allSessions.Count 'steady'
        Invoke-Scenario commands $allSessions.Count 'commands'
        Invoke-Scenario download $allSessions.Count 'download'
        Invoke-Scenario final $allSessions.Count 'final'
    }
    $headers = @{ Authorization = 'Bearer ' + $manifest.operatorToken }
    $diagnostics = Invoke-RestMethod "$target/api/v1/diagnostics/runtime" -Headers $headers -TimeoutSec 5
    $diagnostics | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath (Join-Path $report 'server-diagnostics.json') -Encoding UTF8
    $collection = Invoke-RestMethod "$target/api/v1/submissions/collection/events?examId=$($setup.examId)&afterRosterVersion=0" -Headers $headers -TimeoutSec 5
    $collection | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath (Join-Path $report 'collection-events.json') -Encoding UTF8
    & dotnet $setupDll --audit $fixture (Join-Path $report 'database-audit.json')
    $results.Add([pscustomobject]@{ scenario = 'database-audit'; count = $allSessions.Count; exitCode = $LASTEXITCODE })
} finally {
    $process.Refresh()
    $record = [pscustomobject]@{
        synthetic = $true; productionCapacityCertified = $false; elapsedSeconds = $clock.Elapsed.TotalSeconds
        environment = [pscustomobject]@{ os = [Runtime.InteropServices.RuntimeInformation]::OSDescription; logicalProcessors = [Environment]::ProcessorCount; sameMachineGeneratorAndServer = $true; synchronousReplication = $false; transport = 'HTTP loopback (explicit test exception)'; diagnosticsSamplingSeconds = 2 }
        scenarios = $results.ToArray()
    }
    $record | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $report 'local-run.json') -Encoding UTF8
    if (!$process.HasExited) { Stop-Process -InputObject $process; $process.WaitForExit() }
}
if (@($results | Where-Object exitCode -ne 0).Count -gt 0) { exit 1 }

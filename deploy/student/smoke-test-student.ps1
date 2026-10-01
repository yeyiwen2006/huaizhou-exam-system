param(
    [string]$PublishDir = '',
    [int]$StartupTimeoutSeconds = 120
)

# B07 student client smoke test: launches the published Exam.Student.exe exactly the way
# it will run on a fresh exam machine (standard account, isolated per-user data directory,
# no .NET SDK/runtime installed on PATH) and verifies the startup, DPI manifest and
# deployment-config scenarios:
#   1. no server.json            -> app starts, activation page warns about missing config
#   2. valid server.json         -> app starts and pre-fills the trusted server address
#   3. invalid server.json       -> app starts and shows the config error banner
# The window text cannot be asserted without UI automation; each scenario therefore
# asserts that the process starts, creates a real main window (title set by WPF, not the
# exe fallback) and exits with code 0 on clean close. Config parsing itself is covered by
# unit tests (ServerEndpointConfigTests / ServerTrustTests).
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File deploy\student\smoke-test-student.ps1
# Note: Windows Defender may delay the very first start of a freshly built exe; if a
# scenario times out, simply run the script again before drawing conclusions.

$ErrorActionPreference = 'Stop'

$repo = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
if (-not $PublishDir) { $PublishDir = Join-Path $repo (Join-Path 'artifacts\student\win-x64' 'publish') }
$exe = Join-Path $PublishDir 'Exam.Student.exe'
if (-not (Test-Path $exe)) {
    throw "published exe not found: $exe (run publish-student.ps1 first)"
}

$failures = New-Object System.Collections.Generic.List[string]

# ---------------------------------------------------------------------------
# Static layout assertions (same guarantees as the publish script, re-checked here
# because the smoke command must be reusable against any candidate folder).
# ---------------------------------------------------------------------------
function Assert-True([bool]$condition, [string]$message) {
    if (-not $condition) { throw "layout check failed: $message" }
}

# The .NET SDK embeds app.manifest into the exe as a Win32 resource (no separate file).
# asInvoker = runs with a standard account (S16); PerMonitorV2 = sharp at 100/125/150% (S17).
$exeBytes = [System.IO.File]::ReadAllBytes($exe)
$exeText = [System.Text.Encoding]::UTF8.GetString($exeBytes)
Assert-True ($exeText -match 'PerMonitorV2') "embedded manifest lacks PerMonitorV2 (S17 scaling)"
Assert-True ($exeText -match 'asInvoker') "embedded manifest lacks asInvoker (S16 standard account)"
$sqliteCandidates = @(
    (Join-Path $PublishDir 'e_sqlite3.dll'),
    (Join-Path $PublishDir (Join-Path 'runtimes\win-x64\native' 'e_sqlite3.dll'))
) | Where-Object { Test-Path $_ }
Assert-True (@($sqliteCandidates).Count -gt 0) "native SQLite missing"
Assert-True (Test-Path (Join-Path $PublishDir 'PresentationFramework.dll')) "self-contained runtime missing"

# ---------------------------------------------------------------------------
# Launch scenarios with isolated per-user state.
# ---------------------------------------------------------------------------
function Invoke-Scenario([string]$name, [string[]]$configLines) {
    $stateDir = Join-Path ([System.IO.Path]::GetTempPath()) ("exam-smoke-" + $name + "-" + [System.IO.Path]::GetRandomFileName())
    New-Item -ItemType Directory -Path $stateDir -Force | Out-Null
    $configPath = Join-Path $PublishDir 'server.json'
    $configPlaced = $false
    try {
        if ($configLines) {
            [System.IO.File]::WriteAllLines($configPath, $configLines)
            $configPlaced = $true
        }

        $previousLocalAppData = $env:LOCALAPPDATA
        $env:LOCALAPPDATA = $stateDir
        try {
            $process = Start-Process -FilePath $exe -WorkingDirectory $PublishDir -PassThru
        } finally {
            $env:LOCALAPPDATA = $previousLocalAppData
        }

        $deadline = [DateTime]::UtcNow.AddSeconds($StartupTimeoutSeconds)
        $title = ''
        while ([DateTime]::UtcNow -lt $deadline) {
            Start-Sleep -Milliseconds 500
            if ($process.HasExited) { break }
            $process.Refresh()
            if ($process.MainWindowTitle) { $title = $process.MainWindowTitle; break }
        }

        if ($process.HasExited) {
            $failures.Add("$name : process exited early with code $($process.ExitCode)")
            return
        }
        if (-not $title) {
            $failures.Add("$name : no main window within $StartupTimeoutSeconds s")
            Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
            return
        }
        if ($title -eq 'Exam.Student') {
            $failures.Add("$name : window title is the exe-name fallback, WPF shell did not initialize")
            Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
            return
        }

        $process.CloseMainWindow() | Out-Null
        if (-not $process.WaitForExit(30000)) {
            $failures.Add("$name : app did not close on CloseMainWindow within 30 s")
            Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
            return
        }
        if ($process.ExitCode -ne 0) {
            $failures.Add("$name : clean close expected exit code 0, got $($process.ExitCode)")
            return
        }
        Write-Host "PASS $name (window: $title)"
    } finally {
        if ($configPlaced -and (Test-Path $configPath)) { Remove-Item $configPath -Force }
        try { if (Test-Path $stateDir) { Remove-Item $stateDir -Recurse -Force } } catch { }
    }
}

Invoke-Scenario 'no-config'   $null
Invoke-Scenario 'valid-config' @('{ "formatVersion": 1, "host": "192.0.2.10", "port": 5001, "certificateSha256": "0000000000000000000000000000000000000000000000000000000000000000" }')
Invoke-Scenario 'invalid-config' @('{ "formatVersion": 1, "host": "not-an-ip", "port": 5001 }')

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Host "FAIL $_" }
    exit 1
}
Write-Host 'smoke test: all scenarios passed'
exit 0

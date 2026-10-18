param(
    [string]$PublishDir = '',
    [int]$StartupTimeoutSeconds = 120,
    [switch]$DedicatedInteractiveTestAccount
)

# Student client smoke test: launches the published Exam.Student.exe exactly the way
# it will run on a fresh exam machine (a dedicated standard test account,
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
# The guardian switches the interactive desktop. Run only in a disposable test
# Windows account: changing LOCALAPPDATA does not isolate Windows known folders.
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
Assert-True $DedicatedInteractiveTestAccount.IsPresent "use -DedicatedInteractiveTestAccount in a fresh Windows test account; this test switches desktops"
Assert-True (-not (Test-Path -LiteralPath (Join-Path $PublishDir 'server.json'))) "use an unconfigured test publish directory; existing server.json must be preserved"

# Process.MainWindowHandle enumerates only the caller's desktop. The production
# launcher is now a guardian; find its own child window on the named exam desktop.
Add-Type -TypeDefinition @'
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class ExamDesktopSmoke {
    private delegate bool EnumWindow(IntPtr hwnd, IntPtr state);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern IntPtr OpenDesktopW(string name, uint flags, bool inherit, uint access);
    [DllImport("user32.dll")] private static extern bool CloseDesktop(IntPtr desktop);
    [DllImport("user32.dll")] private static extern bool EnumDesktopWindows(IntPtr desktop, EnumWindow callback, IntPtr state);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern int GetWindowTextW(IntPtr hwnd, StringBuilder text, int length);
    [DllImport("user32.dll")] private static extern bool PostMessageW(IntPtr hwnd, uint message, IntPtr wparam, IntPtr lparam);
    public static IntPtr Find(string desktopName, uint processId) {
        IntPtr desktop=OpenDesktopW(desktopName, 0, false, 0x0041), result=IntPtr.Zero;
        if(desktop==IntPtr.Zero) return result;
        try { EnumDesktopWindows(desktop, (hwnd, state) => {
            uint pid; GetWindowThreadProcessId(hwnd, out pid);
            if(pid==processId && IsWindowVisible(hwnd)) { result=hwnd; return false; }
            return true;
        }, IntPtr.Zero); return result; } finally { CloseDesktop(desktop); }
    }
    public static string Title(IntPtr hwnd) { var text=new StringBuilder(512); GetWindowTextW(hwnd,text,text.Capacity); return text.ToString(); }
    public static void Close(IntPtr hwnd) { if(!PostMessageW(hwnd,0x0010,IntPtr.Zero,IntPtr.Zero)) throw new InvalidOperationException("Cannot close test window"); }
}
'@

# ---------------------------------------------------------------------------
# Launch scenarios with isolated per-user state.
# ---------------------------------------------------------------------------
function Invoke-Scenario([string]$name, [string[]]$configLines) {
    $configPath = Join-Path $PublishDir 'server.json'
    $configPlaced = $false
    $child = $null
    $process = $null
    try {
        if ($configLines) {
            [System.IO.File]::WriteAllLines($configPath, $configLines)
            $configPlaced = $true
        }

        $process = Start-Process -FilePath $exe -WorkingDirectory $PublishDir -WindowStyle Hidden -PassThru

        $deadline = [DateTime]::UtcNow.AddSeconds($StartupTimeoutSeconds)
        $title = ''
        while ([DateTime]::UtcNow -lt $deadline) {
            Start-Sleep -Milliseconds 500
            if ($process.HasExited) { break }
            $process.Refresh()
            $children = @(Get-CimInstance Win32_Process -Filter ("ParentProcessId=" + $process.Id))
            foreach ($candidate in $children) {
                if ($candidate.ExecutablePath -ne [System.IO.Path]::GetFullPath($exe)) { continue }
                $match = [regex]::Match($candidate.CommandLine, '--exam-desktop (HuaizhouExam-[a-f0-9]{32}) ' + $process.Id + ' ')
                if (-not $match.Success) { continue }
                $child = Get-Process -Id $candidate.ProcessId -ErrorAction SilentlyContinue
                $window = [ExamDesktopSmoke]::Find($match.Groups[1].Value, $candidate.ProcessId)
                if ($window -ne [IntPtr]::Zero) { $title = [ExamDesktopSmoke]::Title($window); break }
            }
            if ($title) { break }
        }

        if ($process.HasExited) {
            $failures.Add("$name : process exited early with code $($process.ExitCode)")
            return
        }
        if (-not $title) {
            $failures.Add("$name : no main window within $StartupTimeoutSeconds s")
            return
        }
        if ($title -eq 'Exam.Student') {
            $failures.Add("$name : window title is the exe-name fallback, WPF shell did not initialize")
            return
        }

        [ExamDesktopSmoke]::Close($window)
        if (-not $process.WaitForExit(30000)) {
            $failures.Add("$name : app did not close on CloseMainWindow within 30 s")
            return
        }
        if ($process.ExitCode -ne 0) {
            $failures.Add("$name : clean close expected exit code 0, got $($process.ExitCode)")
            return
        }
        Write-Host "PASS $name (window: $title)"
    } finally {
        # End only the child positively identified under this launcher. Let its
        # guardian restore the original desktop before it exits.
        if ($null -ne $child -and -not $child.HasExited) { $child.Kill(); $child.WaitForExit(10000) | Out-Null }
        if ($null -ne $process -and -not $process.HasExited) { $process.WaitForExit(10000) | Out-Null }
        if ($configPlaced -and (Test-Path -LiteralPath $configPath)) { Remove-Item -LiteralPath $configPath -Force }
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

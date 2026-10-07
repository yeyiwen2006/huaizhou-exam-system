param(
    [ValidateSet('Release', 'Debug')][string]$Configuration = 'Release',
    [ValidateSet('win-x64')][string]$Runtime = 'win-x64',
    [string]$OutputRoot = '',
    [string]$Commit = ''
)

# Student client publish: builds a self-contained, ready-to-copy folder that runs
# on a fresh Windows 10 19041+ x64 machine with a standard (non-admin) account.
# The target machine needs no .NET SDK/runtime, no TeX distribution and no pre-installed
# certificate: the deployment config (server.json, copied from server.example.json by the
# exam staff) carries the trusted server address and, optionally, the server certificate
# SHA-256 fingerprint used for TLS pinning.
#
# Usage (from anywhere):
#   powershell -ExecutionPolicy Bypass -File deploy\student\publish-student.ps1
# Optional: -Commit <sha> to stamp the exact source revision when git is unavailable.

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$repo = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$project = Join-Path $repo 'src\Exam.Student\Exam.Student.csproj'
if (-not (Test-Path $project)) { throw "Exam.Student.csproj not found: $project" }
if (-not $OutputRoot) { $OutputRoot = Join-Path $repo (Join-Path 'artifacts\student' $Runtime) }
$publishDir = Join-Path $OutputRoot 'publish'

if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
New-Item -ItemType Directory -Path $publishDir -Force | Out-Null

# Self-contained publish carries the .NET 10 runtime, WPF and the native SQLite library
# (e_sqlite3.dll) inside the output folder. Windows.Data.Pdf (question rendering) is an
# OS component of Windows 10 19041+, so nothing extra is needed for it.
#
# Lock file handling: the RID-specific restore adds a "net10.0-windows10.0.19041/win-x64"
# section to packages.lock.json, and that section breaks the framework-dependent CI
# restore in locked mode (NU1004). The committed lock files must therefore stay untouched:
# the script backs them up (per-project subdirectories, the files share one name), runs
# the publish restore against them (same pinned versions, plus the SDK-provided runtime
# pack) and restores the originals afterwards.
$lockFiles = @(
    (Join-Path $repo 'src\Exam.Student\packages.lock.json'),
    (Join-Path $repo 'src\Exam.Contracts\packages.lock.json'),
    (Join-Path $repo 'src\Exam.Desktop.Shared\packages.lock.json')
) | Where-Object { Test-Path $_ }
$lockBackupDir = Join-Path ([System.IO.Path]::GetTempPath()) ("exam-publish-locks-" + [System.IO.Path]::GetRandomFileName())
$lockFiles | ForEach-Object {
    $slot = Join-Path $lockBackupDir ([System.IO.Path]::GetFileName([System.IO.Path]::GetDirectoryName($_)))
    New-Item -ItemType Directory -Path $slot -Force | Out-Null
    Copy-Item $_ $slot
}
try {
    & dotnet restore $project -r $Runtime -p:SelfContained=true
    if ($LASTEXITCODE -ne 0) { throw "dotnet restore (RID, self-contained) failed with exit code $LASTEXITCODE" }
    & dotnet publish $project -c $Configuration -r $Runtime --self-contained true -o $publishDir --no-restore
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }
}
finally {
    $lockFiles | ForEach-Object {
        $slot = Join-Path $lockBackupDir ([System.IO.Path]::GetFileName([System.IO.Path]::GetDirectoryName($_)))
        Copy-Item (Join-Path $slot 'packages.lock.json') $_ -Force
    }
    Remove-Item $lockBackupDir -Recurse -Force -ErrorAction SilentlyContinue
}

# ---------------------------------------------------------------------------
# Layout checks: fail the publish if the package would not meet the portable student-client requirements.
# ---------------------------------------------------------------------------
function Assert-True([bool]$condition, [string]$message) {
    if (-not $condition) { throw "publish layout check failed: $message" }
}

$exe = Join-Path $publishDir 'Exam.Student.exe'
Assert-True (Test-Path $exe) "Exam.Student.exe missing"

# The .NET SDK embeds app.manifest into the exe as a Win32 resource (no separate file).
# The embedded manifest must keep asInvoker (standard account, no admin prompt) and
# PerMonitorV2 (sharp UI at 100/125/150% scaling, S17).
$exeBytes = [System.IO.File]::ReadAllBytes($exe)
$exeText = [System.Text.Encoding]::UTF8.GetString($exeBytes)
Assert-True ($exeText -match 'PerMonitorV2') "embedded manifest lacks PerMonitorV2 DPI awareness"
Assert-True ($exeText -match 'asInvoker') "embedded manifest lacks asInvoker (would require admin)"

# Self-contained runtime evidence: WPF assemblies and the host policy must be inside
# the folder, and the native SQLite library must be present at the layout root.
Assert-True (Test-Path (Join-Path $publishDir 'PresentationFramework.dll')) "PresentationFramework.dll missing (not self-contained?)"
Assert-True (Test-Path (Join-Path $publishDir 'WindowsBase.dll')) "WindowsBase.dll missing (not self-contained?)"
Assert-True (Test-Path (Join-Path $publishDir 'hostpolicy.dll')) "hostpolicy.dll missing (not self-contained?)"
$sqliteCandidates = @(
    (Join-Path $publishDir 'e_sqlite3.dll'),
    (Join-Path $publishDir (Join-Path 'runtimes\win-x64\native' 'e_sqlite3.dll'))
) | Where-Object { Test-Path $_ }
Assert-True (@($sqliteCandidates).Count -gt 0) "native SQLite (e_sqlite3.dll) missing"

# A pristine release folder must NOT contain a deployed server.json: the exam staff
# creates it from server.example.json on the target machine, so no real address or
# credential can leak into the repository or the artifact.
Assert-True (-not (Test-Path (Join-Path $publishDir 'server.json'))) "server.json must not be pre-baked into the package"
Assert-True (-not (Test-Path (Join-Path $publishDir 'state.db'))) "local state database must not ship with the package"

# ---------------------------------------------------------------------------
# Deployment helpers and build provenance.
# ---------------------------------------------------------------------------
Copy-Item (Join-Path $PSScriptRoot 'server.example.json') -Destination $publishDir
Copy-Item (Join-Path $PSScriptRoot 'README.md') -Destination $publishDir

if (-not $Commit) {
    try { $Commit = (& git -C $repo rev-parse HEAD 2>$null) } catch { $Commit = $null }
    if (-not $Commit) { $Commit = 'unknown' }
}
$sdkVersion = (& dotnet --version)
$buildInfo = [ordered]@{
    builtAtUtc    = ([DateTime]::UtcNow.ToString('o'))
    configuration = $Configuration
    runtime       = $Runtime
    selfContained = $true
    targetWindows = 'Windows 10 19041+ x64, standard account, no admin'
    commit        = $Commit
    sdkVersion    = $sdkVersion
}
$buildInfo | ConvertTo-Json | Set-Content -Path (Join-Path $publishDir 'build-info.json')

$zipPath = Join-Path $OutputRoot ("Exam.Student-$Runtime.zip")
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Compress-Archive -Path (Join-Path $publishDir '*') -DestinationPath $zipPath

Write-Host "publish folder: $publishDir"
Write-Host "zip archive   : $zipPath"
Write-Host "smoke test    : powershell -ExecutionPolicy Bypass -File deploy\student\smoke-test-student.ps1"
exit 0

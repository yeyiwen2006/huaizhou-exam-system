"""F05 product scripts: synthetic reports/files and mocked native invocations only."""
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
SCRIPTS = ROOT / "deploy/postgresql/scripts"
POWERSHELL = shutil.which("pwsh") or shutil.which("powershell")


@unittest.skipUnless(POWERSHELL, "PowerShell is not installed; no product script verification performed")
class PostgreSqlOperationsTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="exam-f05-synthetic-")
        self.root = Path(self.temp.name)

    def tearDown(self):
        self.temp.cleanup()

    def ps(self, script, arguments=(), environment=None):
        return subprocess.run([POWERSHELL, "-NoProfile", "-NonInteractive", "-File", str(script), *map(str, arguments)],
                              capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=30, env=environment)

    def harness(self, body, arguments=()):
        path = self.root / "harness.ps1"
        path.write_text(body, encoding="utf-8")
        return self.ps(path, [SCRIPTS, self.root, *arguments])

    @staticmethod
    def reports():
        primary = dict(inRecovery=False, systemIdentifier="123456789", expectedSystemIdentifier="123456789",
                       database="exam", versionNumber=180001, synchronousCommit="remote_apply", fsync="on",
                       fullPageWrites="on", synchronousStandbyNames="FIRST 1 (exam_standby)", flushLsn="1/100",
                       expectedHost="127.0.0.1", expectedPort=5432, expectedSlot="exam_standby", timeline=1,
                       standbys=[dict(applicationName="exam_standby", state="streaming", syncState="sync", replayLsn="1/100")],
                       maxSlotWalKeepBytes=1024, slots=[dict(active=True, walStatus="reserved", retainedBytes=16, safeWalBytes=1008)],
                       archiveMode="on", archiveCount=1, lastArchiveFailure=None, lastArchiveSuccess="2026-10-05T00:00:00Z")
        standby = dict(inRecovery=True, systemIdentifier="123456789", database="exam", versionNumber=180001,
                       receiveLsn="1/100", replayLsn="1/100", replayPaused=False,
                       receiver=dict(status="streaming", timeline=1, senderHost="127.0.0.1", senderPort=5432, slot="exam_standby"))
        return primary, standby

    def errors(self, primary, standby):
        fixture = self.root / "report.json"
        fixture.write_text(json.dumps(dict(primary=primary, standby=standby)), encoding="utf-8")
        result = self.harness('''param($Scripts, $Root)
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $Scripts 'Postgresql.Operations.psm1') -Force -DisableNameChecking
$fixture = Get-Content -LiteralPath (Join-Path $Root 'report.json') -Raw | ConvertFrom-Json
$errors = Get-ReplicationErrors $fixture.primary $fixture.standby 'exam_standby' 1024
ConvertTo-Json -InputObject @($errors) -Compress
''')
        self.assertEqual(0, result.returncode, result.stderr)
        return json.loads(result.stdout)

    def test_exact_peer_and_full_lsn_boundary(self):
        primary, standby = self.reports()
        for name in ['exam_standby', '"exam_standby"', 'FIRST 1 (exam_standby)', 'ANY 1 ("exam_standby")']:
            with self.subTest(name=name):
                primary["synchronousStandbyNames"] = name
                self.assertEqual([], self.errors(primary, standby))
        for name in ["exam_standby_other", "FIRST 1 (*)", "ANY 1 (exam_standby, other)"]:
            with self.subTest(name=name):
                primary["synchronousStandbyNames"] = name
                self.assertTrue(self.errors(primary, standby))
        primary, standby = self.reports()
        primary["flushLsn"] = "2/1"
        standby["receiveLsn"] = standby["replayLsn"] = "1/FFFFFFFF"
        self.assertTrue(self.errors(primary, standby))
        result = self.harness('''param($Scripts, $Root)
Import-Module (Join-Path $Scripts 'Postgresql.Operations.psm1') -Force -DisableNameChecking
ConvertFrom-PgLsn 'FFFFFFFF/FFFFFFFF'
''')
        self.assertEqual("18446744073709551615", result.stdout.strip())

    def test_roles_cluster_patch_timeline_and_wal_fail_closed(self):
        cases = [("primary", "inRecovery", True), ("standby", "systemIdentifier", "999"),
                 ("primary", "expectedSystemIdentifier", "999"), ("standby", "versionNumber", 180002),
                 ("primary", "fsync", "off"), ("primary", "maxSlotWalKeepBytes", -1),
                 ("primary", "archiveCount", 0), ("standby", "replayPaused", True)]
        for target, key, value in cases:
            with self.subTest(target=target, key=key):
                primary, standby = self.reports()
                (primary if target == "primary" else standby)[key] = value
                self.assertTrue(self.errors(primary, standby))
        for key, value in [("walStatus", "lost"), ("safeWalBytes", None), ("active", False), ("retainedBytes", 1024)]:
            with self.subTest(key=key):
                primary, standby = self.reports(); primary["slots"][0][key] = value
                self.assertTrue(self.errors(primary, standby))
        for key, value in [("timeline", 2), ("senderHost", "127.0.0.9"), ("slot", "other"), ("status", "stopped")]:
            primary, standby = self.reports(); standby["receiver"][key] = value
            self.assertTrue(self.errors(primary, standby))
        primary, standby = self.reports()
        del primary["flushLsn"]
        self.assertTrue(self.errors(primary, standby))

    def test_storage_thresholds_and_missing_paths_do_not_create_data(self):
        arguments = [flag for key in ["Data", "Archive", "Resource", "Backup", "ProtectedConfiguration"]
                     for flag in [f"-{key}Directory", self.root]]
        result = self.ps(SCRIPTS / "Test-StorageCapacity.ps1", [*arguments, "-MinimumAvailableBytes", 1, "-MinimumAvailablePercent", 1])
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual(5, len(json.loads(result.stdout)["volumes"]))
        result = self.ps(SCRIPTS / "Test-StorageCapacity.ps1", [*arguments, "-MinimumAvailableBytes", 9223372036854775807])
        self.assertNotEqual(0, result.returncode)
        self.assertFalse(json.loads(result.stdout)["ready"])
        missing = self.root / "missing"
        arguments[1] = missing
        result = self.ps(SCRIPTS / "Test-StorageCapacity.ps1", arguments)
        self.assertNotEqual(0, result.returncode); self.assertFalse(missing.exists())

    def test_archive_and_restore_are_idempotent_and_never_overwrite_mismatch(self):
        source = self.root / "source"; source.write_bytes(b"synthetic-WAL-only")
        archive = self.root / "archive"; archive.mkdir()
        wal = "000000010000000000000001"
        args = ["-SourcePath", source, "-WalFileName", wal, "-ArchiveRoot", archive]
        for _ in range(2):
            result = self.ps(SCRIPTS / "Invoke-WalArchiveCommand.ps1", args)
            self.assertEqual(0, result.returncode, result.stderr)
        source.write_bytes(b"different-synthetic-WAL")
        self.assertNotEqual(0, self.ps(SCRIPTS / "Invoke-WalArchiveCommand.ps1", args).returncode)
        self.assertEqual(b"synthetic-WAL-only", (archive / wal).read_bytes())
        destination = self.root / "restored"
        restore = ["-WalFileName", wal, "-DestinationPath", destination, "-ArchiveRoot", archive]
        self.assertEqual(0, self.ps(SCRIPTS / "Restore-WalArchive.ps1", restore).returncode)
        destination.write_bytes(b"preserve-original")
        self.assertNotEqual(0, self.ps(SCRIPTS / "Restore-WalArchive.ps1", restore).returncode)
        self.assertEqual(b"preserve-original", destination.read_bytes())
        for invalid in ["../outside", wal + ":secret", ".", ""]:
            args[3] = invalid
            self.assertNotEqual(0, self.ps(SCRIPTS / "Invoke-WalArchive.ps1", args).returncode)

    def test_initialization_failure_preserves_partial_data_and_racing_slot(self):
        result = self.harness('''param($Scripts, $Root)
$ErrorActionPreference = 'Stop'
$bin = Join-Path $Root 'bin'; New-Item -ItemType Directory -Path $bin | Out-Null
$psql = Join-Path $bin 'psql.exe'; $backup = Join-Path $bin 'pg_basebackup.exe'
Set-Content -LiteralPath $psql -Value 'synthetic'; Set-Content -LiteralPath $backup -Value 'synthetic'
$cert = Join-Path $Root 'ca.crt'; Set-Content -LiteralPath $cert -Value 'synthetic-public-only'
$env:PGSSLMODE = 'verify-full'; $env:PGSSLROOTCERT = $cert
$global:commands = @()
function FakePsql {
    $global:LASTEXITCODE = 0
    if ($args[0] -eq '--version') { 'psql (PostgreSQL) 18.1'; return }
    $global:commands += ($args -join ' '); '{"systemIdentifier":"123456789","inRecovery":false,"versionNumber":180001,"slotState":"missing"}'
}
function FakeBackup {
    $global:LASTEXITCODE = 0
    if ($args[0] -eq '--version') { 'pg_basebackup (PostgreSQL) 18.1'; return }
    $target = $args[[Array]::IndexOf($args, '--pgdata') + 1]
    Set-Content -LiteralPath (Join-Path $target 'partial') -Value 'preserve'
    $global:LASTEXITCODE = 1
}
Set-Alias -Name $psql -Value FakePsql; Set-Alias -Name $backup -Value FakeBackup
try {
    & (Join-Path $Scripts 'Initialize-Standby.ps1') -PostgresBinDirectory $bin -StandbyDataDirectory (Join-Path $Root 'standby') -PrimaryHost '127.0.0.1' -ReplicationUser 'exam_replication' -ExpectedSystemIdentifier '123456789'
    throw 'Expected initialization rejection.'
} catch {
    if ($_.Exception.Message -notmatch 'Partial data and replication slots are preserved') { throw }
}
if (-not (Test-Path -LiteralPath (Join-Path $Root 'standby/partial'))) { throw 'Partial data lost.' }
if ($global:commands.Count -ne 1 -or ($global:commands -join ' ') -match 'drop_replication_slot') { throw 'Slot modified after failure.' }
try {
    & (Join-Path $Scripts 'Initialize-Standby.ps1') -PostgresBinDirectory $bin -StandbyDataDirectory (Join-Path $Root 'standby') -PrimaryHost '127.0.0.1' -ReplicationUser 'exam_replication' -ExpectedSystemIdentifier '123456789'
    throw 'Expected nonempty-directory rejection.'
} catch { if ($_.Exception.Message -notmatch 'absent or empty') { throw } }
'preserved'
''')
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertIn("preserved", result.stdout)

    def test_maintenance_rejects_remote_or_ambiguous_operations_before_calling_cli(self):
        fake = self.root / "not-executable.exe"; fake.write_bytes(b"synthetic-placeholder")
        base = ["-CliPath", fake, "-ServerId", "synthetic", "-NodeId", "primary"]
        result = self.ps(SCRIPTS / "Invoke-ExamMaintenance.ps1", [*base, "-Operation", "status", "-ManagementUrl", "https://192.0.2.1:5010"])
        self.assertNotEqual(0, result.returncode)
        self.assertIn("local HTTPS", result.stderr)
        result = self.ps(SCRIPTS / "Invoke-ExamMaintenance.ps1", [*base, "-Operation", "restore", "-ManagementUrl", "https://127.0.0.1:5010", "-RequestId", "new-id"])
        self.assertNotEqual(0, result.returncode)
        self.assertIn("original JSON", result.stderr)

    def test_paths_reject_relative_and_ancestor_junctions(self):
        result = self.harness('''param($Scripts, $Root)
Import-Module (Join-Path $Scripts 'Postgresql.Operations.psm1') -Force -DisableNameChecking
try { Assert-ControlledPath 'relative/path' -AllowMissing; exit 9 } catch { 'rejected' }
''')
        self.assertEqual(0, result.returncode, result.stderr)
        if os.name == "nt":
            result = self.harness('''param($Scripts, $Root)
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $Scripts 'Postgresql.Operations.psm1') -Force -DisableNameChecking
$real = Join-Path $Root 'real'; New-Item -ItemType Directory -Path $real | Out-Null
$link = Join-Path $Root 'junction'; New-Item -ItemType Junction -Path $link -Target $real | Out-Null
try { Assert-ControlledPath (Join-Path $link 'child') -AllowMissing; exit 9 } catch { 'rejected' }
''')
            self.assertEqual(0, result.returncode, result.stderr)

    def test_readiness_entry_uses_expected_identity_and_never_accepts_password_arguments(self):
        primary, standby = self.reports()
        for key in ["expectedSystemIdentifier", "expectedHost", "expectedPort", "expectedSlot"]:
            del primary[key]
        (self.root / "primary.json").write_text(json.dumps(primary), encoding="utf-8")
        (self.root / "standby.json").write_text(json.dumps(standby), encoding="utf-8")
        result = self.harness('''param($Scripts, $Root)
$ErrorActionPreference = 'Stop'
$tool = Join-Path $Root 'psql.exe'; Set-Content -LiteralPath $tool -Value 'synthetic'
function FakePsql {
    $global:LASTEXITCODE = 0
    if (($args -join ' ') -match '127.0.0.2') { Get-Content -LiteralPath (Join-Path $Root 'standby.json') -Raw }
    else { Get-Content -LiteralPath (Join-Path $Root 'primary.json') -Raw }
}
Set-Alias -Name $tool -Value FakePsql
& (Join-Path $Scripts 'Test-ReplicationReadiness.ps1') -PsqlPath $tool -PrimaryConnectionUri 'postgresql://operator@127.0.0.1:5432/exam?sslmode=verify-full' -StandbyConnectionUri 'postgresql://operator@127.0.0.2:5432/exam?sslmode=verify-full' -ExpectedSystemIdentifier '123456789' -MaximumSlotWalBytes 1024
''')
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertTrue(json.loads(result.stdout)["ready"])
        tool = self.root / "psql.exe"
        result = self.ps(SCRIPTS / "Test-ReplicationReadiness.ps1", ["-PsqlPath", tool,
            "-PrimaryConnectionUri", "postgresql://operator:synthetic-not-secret@127.0.0.1/exam?sslmode=verify-full",
            "-StandbyConnectionUri", "postgresql://operator@127.0.0.2/exam?sslmode=verify-full", "-ExpectedSystemIdentifier", "123456789"])
        self.assertNotEqual(0, result.returncode)
        self.assertIn("without passwords", result.stderr)

    def test_maintenance_delegates_original_request_and_native_exit_code(self):
        original = b'{"requestId":"fixed-request","backupId":"synthetic-backup"}'
        (self.root / "request.json").write_bytes(original)
        result = self.harness('''param($Scripts, $Root)
$ErrorActionPreference = 'Stop'
$cli = Join-Path $Root 'Exam.Cli.exe'; Set-Content -LiteralPath $cli -Value 'synthetic'
function FakeCli { ConvertTo-Json -InputObject @($args) -Compress; $global:LASTEXITCODE = 17 }
Set-Alias -Name $cli -Value FakeCli
& (Join-Path $Scripts 'Invoke-ExamMaintenance.ps1') -CliPath $cli -Operation restore -ServerId synthetic -NodeId primary -ManagementUrl 'https://127.0.0.1:5010' -InputPath (Join-Path $Root 'request.json')
exit $LASTEXITCODE
''')
        self.assertEqual(17, result.returncode, result.stderr)
        arguments = json.loads(result.stdout)
        self.assertEqual(["management", "restore"], arguments[:2])
        self.assertEqual(str(self.root / "request.json"), arguments[-1])
        self.assertEqual(original, (self.root / "request.json").read_bytes())

    def test_configuration_generation_uses_public_cert_and_preserves_existing_output(self):
        result = self.harness('''param($Scripts, $Root)
$ErrorActionPreference = 'Stop'
$rsa = [Security.Cryptography.RSA]::Create(2048)
try {
    $request = [Security.Cryptography.X509Certificates.CertificateRequest]::new('CN=Synthetic F05 CA', $rsa, [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pkcs1)
    $cert = $request.CreateSelfSigned([DateTimeOffset]::UtcNow.AddDays(-1), [DateTimeOffset]::UtcNow.AddDays(1))
    try { [IO.File]::WriteAllBytes((Join-Path $Root 'ca.cer'), $cert.Export([Security.Cryptography.X509Certificates.X509ContentType]::Cert)) }
    finally { $cert.Dispose() }
} finally { $rsa.Dispose() }
$output = Join-Path $Root 'generated'
& (Join-Path $Scripts 'New-PostgresqlDeployment.ps1') -OutputDirectory $output -PrimaryAddress 127.0.0.1 -StandbyAddress 127.0.0.2 -DeploymentRoot $Root -ArchiveRoot $Root -CaCertificatePath (Join-Path $Root 'ca.cer') -MaxSlotWalKeepSizeMB 1234
$hash = (Get-FileHash -LiteralPath (Join-Path $output 'primary.conf.fragment')).Hash
try {
    & (Join-Path $Scripts 'New-PostgresqlDeployment.ps1') -OutputDirectory $output -PrimaryAddress 127.0.0.1 -StandbyAddress 127.0.0.2 -DeploymentRoot $Root -ArchiveRoot $Root -CaCertificatePath (Join-Path $Root 'ca.cer')
    throw 'Expected existing output rejection.'
} catch { if ($_.Exception.Message -notmatch 'never overwritten') { throw } }
if ($hash -ne (Get-FileHash -LiteralPath (Join-Path $output 'primary.conf.fragment')).Hash) { throw 'Output changed.' }
''')
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertFalse(json.loads(result.stdout)["applied"])
        text = (self.root / "generated/primary.conf.fragment").read_text(encoding="utf-8")
        self.assertIn("max_slot_wal_keep_size = '1234MB'", text)
        self.assertNotIn("{{", text)
        self.assertNotIn("password=", text)

    def test_monitor_reports_missing_dependency_and_never_emits_a_green_result(self):
        configuration = dict(psqlPath=str(self.root / "missing-psql.exe"),
            primaryConnectionUri="postgresql://operator@127.0.0.1/exam?sslmode=verify-full",
            standbyConnectionUri="postgresql://operator@127.0.0.2/exam?sslmode=verify-full",
            expectedSystemIdentifier="123456789", standbyApplicationName="exam_standby", replicationSlot="exam_standby",
            maximumSlotWalBytes=1024, storage={key + "Directory": str(self.root)
                for key in ["data", "archive", "resource", "backup", "protectedConfiguration"]})
        configuration["storage"].update(minimumAvailableBytes=1, minimumAvailablePercent=1)
        path = self.root / "check.json"; path.write_text(json.dumps(configuration), encoding="utf-8")
        result = self.ps(SCRIPTS / "Test-ExamDeployment.ps1", ["-ConfigurationPath", path])
        self.assertNotEqual(0, result.returncode, result.stderr)
        report = json.loads(result.stdout)
        self.assertFalse(report["ready"])
        self.assertFalse(report["replication"]["report"]["ready"])
        self.assertTrue(report["storage"]["report"]["ready"])


if __name__ == "__main__":
    unittest.main()

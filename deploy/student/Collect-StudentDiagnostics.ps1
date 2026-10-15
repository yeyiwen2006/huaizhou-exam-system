param(
    [string]$DataDirectory = (Join-Path $env:LOCALAPPDATA 'HuaizhouExam\Student'),
    [string]$OutputDirectory = (Join-Path ([Environment]::GetFolderPath('Desktop')) '学生端错误记录'),
    [string]$SqliteLibrary = (Join-Path $PSScriptRoot 'e_sqlite3.dll')
)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$report = [ordered]@{ collectedAt = (Get-Date).ToString('o'); readOnly = $true }
$database = Join-Path $DataDirectory 'state.db'
$identityPath = $database + '.identity'
$report.databaseExists = Test-Path -LiteralPath $database
$report.identityExists = Test-Path -LiteralPath $identityPath
try {
    $identity = if ($report.identityExists) { Get-Content -LiteralPath $identityPath -Raw -Encoding UTF8 | ConvertFrom-Json } else { $null }
    $report.identitySchemaVersion = if ($null -ne $identity) { $identity.schemaVersion } else { $null }
    if ($report.databaseExists) {
        $libraryLiteral = (Resolve-Path -LiteralPath $SqliteLibrary).Path.Replace('\', '\\').Replace('"', '\"')
        Add-Type -TypeDefinition @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public static class ExamStudentReadOnlySqlite {
    [DllImport("$libraryLiteral", CallingConvention=CallingConvention.Cdecl)] static extern int sqlite3_open_v2(byte[] path, out IntPtr db, int flags, IntPtr vfs);
    [DllImport("$libraryLiteral", CallingConvention=CallingConvention.Cdecl)] static extern int sqlite3_close(IntPtr db);
    [DllImport("$libraryLiteral", CallingConvention=CallingConvention.Cdecl)] static extern int sqlite3_prepare_v2(IntPtr db, byte[] sql, int size, out IntPtr stmt, IntPtr tail);
    [DllImport("$libraryLiteral", CallingConvention=CallingConvention.Cdecl)] static extern int sqlite3_step(IntPtr stmt);
    [DllImport("$libraryLiteral", CallingConvention=CallingConvention.Cdecl)] static extern int sqlite3_finalize(IntPtr stmt);
    [DllImport("$libraryLiteral", CallingConvention=CallingConvention.Cdecl)] static extern int sqlite3_column_count(IntPtr stmt);
    [DllImport("$libraryLiteral", CallingConvention=CallingConvention.Cdecl)] static extern IntPtr sqlite3_column_text(IntPtr stmt, int column);
    static byte[] Utf8(string value) { return Encoding.UTF8.GetBytes(value + "\0"); }
    static string ReadText(IntPtr value) {
        if (value == IntPtr.Zero) return null;
        int length = 0;
        while (Marshal.ReadByte(value, length) != 0) length++;
        var bytes = new byte[length];
        Marshal.Copy(value, bytes, 0, length);
        return Encoding.UTF8.GetString(bytes);
    }
    public static string[][] Query(string path, string sql) {
        IntPtr db = IntPtr.Zero, stmt = IntPtr.Zero;
        try {
            int code = sqlite3_open_v2(Utf8(path), out db, 1, IntPtr.Zero); // SQLITE_OPEN_READONLY; never create a database.
            if (code != 0) throw new InvalidOperationException("SQLite open code " + code);
            code = sqlite3_prepare_v2(db, Utf8(sql), -1, out stmt, IntPtr.Zero);
            if (code != 0) throw new InvalidOperationException("SQLite prepare code " + code);
            var rows = new List<string[]>();
            while ((code = sqlite3_step(stmt)) == 100) {
                var row = new string[sqlite3_column_count(stmt)];
                for (int column = 0; column < row.Length; column++) row[column] = ReadText(sqlite3_column_text(stmt, column));
                rows.Add(row);
            }
            if (code != 101) throw new InvalidOperationException("SQLite read code " + code);
            return rows.ToArray();
        } finally {
            if (stmt != IntPtr.Zero) sqlite3_finalize(stmt);
            if (db != IntPtr.Zero) sqlite3_close(db);
        }
    }
}
"@
        $rows = [ExamStudentReadOnlySqlite]::Query($database, 'PRAGMA user_version;')
        $report.databaseSchemaVersion = $rows[0][0]
        $rows = [ExamStudentReadOnlySqlite]::Query($database, "SELECT name,sql FROM sqlite_master WHERE type='table' ORDER BY name;")
        $report.tables = @($rows | ForEach-Object { [ordered]@{ name = $_[0]; definition = $_[1] } })
        $rows = [ExamStudentReadOnlySqlite]::Query($database, 'SELECT database_id FROM store_identity WHERE singleton=1;')
        $report.databaseIdentityMatches = $rows.Count -eq 1 -and $null -ne $identity -and $rows[0][0] -ceq $identity.databaseId
        $counts = [ordered]@{}
        foreach ($table in @('answer_state', 'pending_snapshot', 'fixed_submission', 'preparation', 'paper_update_review', 'upload_outbox')) {
            if ($report.tables.name -contains $table) {
                $rows = [ExamStudentReadOnlySqlite]::Query($database, "SELECT COUNT(*) FROM $table;")
                $counts[$table] = $rows[0][0]
            }
        }
        $report.recordCounts = $counts
    }
} catch { $report.readError = $_.Exception.Message }
$outputPath = Join-Path $OutputDirectory '本机存储状态.json'
$report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $outputPath -Encoding UTF8
$logDirectory = Join-Path $DataDirectory 'logs'
if (Test-Path -LiteralPath $logDirectory) {
    Get-ChildItem -LiteralPath $logDirectory -Filter 'crash-*.txt' -File | Copy-Item -Destination $OutputDirectory -Force
}
Write-Host '检查结果和错误记录已保存到桌面的“学生端错误记录”文件夹。'
Write-Host '只读取了版本、表结构和记录数量，没有读取答题内容，也没有修改原数据库。'

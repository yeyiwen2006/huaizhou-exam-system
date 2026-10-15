using System.IO;
using Exam.Student.Core;
using Microsoft.Data.Sqlite;

namespace Exam.StudentUnitTests;

public sealed partial class ExamPackageManagerTests
{
    [Fact]
    public async Task IncompatibleLocalDatabaseDoesNotCrashEntryOrOverwriteRecords()
    {
        using var fixture = await Fixture.CreateAsync();
        var path = Path.Combine(fixture.Root, "version-mismatch.db");
        var key = new LocalStateKey("server", "exam", "candidate");
        using (var original = new SqliteLocalStateStore(path))
        {
            Assert.True((await original.SavePreparationAsync(key,
                new PreparationRecord(1, 1, "original.pkg", null, "Prepared", DateTimeOffset.UtcNow))).Succeeded);
        }
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = path, Pooling = false }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version=2;";
            command.ExecuteNonQuery();
        }
        var originalDatabase = File.ReadAllBytes(path);
        var originalIdentity = File.ReadAllBytes(path + ".identity");
        using (var local = new SqliteLocalStateStore(path))
        await using (var runtime = new StudentExamRuntime(fixture.Manager(), fixture.Device, local, "candidate"))
        {
            Assert.False(await runtime.InitializeLocalRecoveryAsync());
            Assert.False(runtime.LocalRecoveryChecked);
            Assert.NotNull(runtime.LocalStateError);
            Assert.Null(runtime.Answers);
            Assert.Null(runtime.Manager.Participation);
            Assert.False(runtime.Manager.ExamStarted);
            Assert.Equal(0, fixture.Api.DownloadCalls);
        }
        Assert.Equal(originalDatabase, File.ReadAllBytes(path));
        Assert.Equal(originalIdentity, File.ReadAllBytes(path + ".identity"));
    }
}

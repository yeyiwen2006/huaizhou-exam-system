using System.Text.Json;

namespace Exam.LoadTests.Tests;

public sealed class LocalFixtureValidationTests
{
    [Theory]
    [InlineData("127.0.0.1", false, false, true)]
    [InlineData("example.org", false, false, false)]
    [InlineData("127.0.0.1", true, false, false)]
    [InlineData("127.0.0.1", false, true, false)]
    public async Task FixtureValidationRejectsWrongActualDatabaseOrManifest(string host, bool wrongDatabase, bool wrongExam, bool valid)
    {
        var directory = Path.Combine(Path.GetTempPath(), "exam-capacity-validation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var database = "exam_load_" + Guid.NewGuid().ToString("N");
            File.WriteAllText(Path.Combine(directory, "setup.json"), JsonSerializer.Serialize(new
            { synthetic = true, database, examId = "synthetic-exam", count = 1 }));
            await FixtureConfiguration.WriteAsync(Path.Combine(directory, "local-server.json"), new
            {
                ConnectionStrings = new { Exam = $"Host={host};Database={(wrongDatabase ? "postgres" : database)};Username=synthetic" },
                ExamServer = new { ServerId = "server", NodeId = "node", ServerEpoch = 1 }
            });
            File.WriteAllText(Path.Combine(directory, "sessions.json"), JsonSerializer.Serialize(new
            {
                synthetic = true, serverId = "server", serverEpoch = 1,
                sessions = new[] { new { examId = wrongExam ? "other" : "synthetic-exam" } }
            }));
            if (valid) LocalFixtureValidation.Validate(directory);
            else Assert.Throws<InvalidOperationException>(() => LocalFixtureValidation.Validate(directory));
        }
        finally
        {
            // Only the three known files created by this test; no recursive directory deletion.
            foreach (var name in new[] { "setup.json", "local-server.json", "sessions.json" }) File.Delete(Path.Combine(directory, name));
            Directory.Delete(directory);
        }
    }
}

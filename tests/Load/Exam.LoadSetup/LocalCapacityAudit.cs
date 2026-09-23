using System.Text.Json;
using System.Text.RegularExpressions;
using Exam.Contracts.Common;
using Exam.Contracts.Submissions;
using Npgsql;

internal static partial class LocalCapacityAudit
{
    [GeneratedRegex("^exam_load_[a-f0-9]{32}$", RegexOptions.CultureInvariant)]
    private static partial Regex DatabaseName();

    public static async Task<int> RunAsync(string fixtureDirectory, string outputPath)
    {
        LocalFixtureValidation.Validate(fixtureDirectory);
        if (File.Exists(outputPath)) throw new InvalidOperationException("Audit report already exists.");
        using var setup = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(fixtureDirectory, "setup.json")));
        using var configuration = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(fixtureDirectory, "local-server.json")));
        var database = setup.RootElement.GetProperty("database").GetString()!;
        var examId = setup.RootElement.GetProperty("examId").GetString()!;
        var expected = setup.RootElement.GetProperty("count").GetInt32();
        var questionCount = setup.RootElement.GetProperty("questionCount").GetInt32();
        var options = new NpgsqlConnectionStringBuilder(configuration.RootElement.GetProperty("ConnectionStrings").GetProperty("Exam").GetString());
        if (!setup.RootElement.GetProperty("synthetic").GetBoolean() || !DatabaseName().IsMatch(database) ||
            options.Database != database || options.Host is not ("127.0.0.1" or "localhost" or "::1"))
            throw new InvalidOperationException("Audit requires a local isolated fixture.");
        await using var connection = new NpgsqlConnection(options.ConnectionString);
        await connection.OpenAsync();
        // A repeatable read snapshot makes all counts and answer checks refer to the same state.
        await using var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead);
        await using (var readOnly = new NpgsqlCommand("SET TRANSACTION READ ONLY", connection, transaction))
            await readOnly.ExecuteNonQueryAsync();
        var counts = new Dictionary<string, long>();
        await using (var query = new NpgsqlCommand("""
            SELECT 'actual', count(*) FROM exam.participations WHERE exam_id=@exam AND business_state <> 'not_participating'
            UNION ALL SELECT 'submitted', count(*) FROM exam.participations WHERE exam_id=@exam AND business_state='submitted'
            UNION ALL SELECT 'finals', count(*) FROM exam.final_submissions WHERE exam_id=@exam
            UNION ALL SELECT 'uniqueReceipts', count(DISTINCT receipt_id) FROM exam.final_submissions WHERE exam_id=@exam
            UNION ALL SELECT 'gradingTasks', count(*) FROM exam.grading_outbox g JOIN exam.final_submissions s USING (submission_id) WHERE s.exam_id=@exam
            UNION ALL SELECT 'completionEvents', count(*) FROM exam.collection_completion_events WHERE exam_id=@exam AND is_authoritative
            UNION ALL SELECT 'pendingCollectionChecks', count(*) FROM exam.collection_pending_checks WHERE exam_id=@exam;
            """, connection, transaction))
        {
            query.Parameters.AddWithValue("exam", examId);
            await using var reader = await query.ExecuteReaderAsync();
            while (await reader.ReadAsync()) counts.Add(reader.GetString(0), reader.GetInt64(1));
        }
        var invalidAnswerSets = 0;
        await using (var query = new NpgsqlCommand("SELECT frozen_answers FROM exam.final_submissions WHERE exam_id=@exam", connection, transaction))
        {
            query.Parameters.AddWithValue("exam", examId);
            await using var reader = await query.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var answers = JsonSerializer.Deserialize<Dictionary<string, AnswerValue>>(reader.GetString(0), JsonDefaults.Create());
                if (answers is null || answers.Count != questionCount || Enumerable.Range(1, questionCount).Any(q =>
                        !answers.TryGetValue("q" + q, out var value) || value.Kind != AnswerValue.Selected("A").Kind || value.OptionIds.Count != 1 ||
                        value.OptionIds[0] != new[] { "A", "B", "C", "D" }[q % 4])) invalidAnswerSets++;
            }
        }
        double? collectionDelayMs;
        await using (var query = new NpgsqlCommand("""
            SELECT EXTRACT(EPOCH FROM (MAX(e.completed_at) - MAX(s.received_at))) * 1000
              FROM exam.collection_completion_events e JOIN exam.final_submissions s USING (exam_id)
             WHERE e.exam_id=@exam AND e.is_authoritative;
            """, connection, transaction))
        {
            query.Parameters.AddWithValue("exam", examId);
            var scalar = await query.ExecuteScalarAsync();
            collectionDelayMs = scalar is null or DBNull ? null : Convert.ToDouble(scalar);
        }
        await transaction.CommitAsync();
        var passed = counts.Where(p => p.Key is "actual" or "submitted" or "finals" or "uniqueReceipts" or "gradingTasks").All(p => p.Value == expected)
            && counts["completionEvents"] == 1 && counts["pendingCollectionChecks"] == 0 && invalidAnswerSets == 0;
        var report = new { synthetic = true, passed, expectedParticipants = expected, questionCount, counts,
            invalidAnswerSets, collectionPersistedAfterLastReceiptTimestampMs = collectionDelayMs,
            limitations = new[] { "Read-only independent storage counts/answer shape audit, not dual-host durability certification.",
                "received_at is transaction processing time, not commit or operator-GUI display time; delay is diagnostic only.",
                "Final answers are compared question by question to the runner's stable Answers(0,0) payload; progress versions/payload are checked in each HTTP response, not inferred from row counts." } };
        await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(report, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
        Console.WriteLine($"Independent database audit: passed={passed}; finals={counts["finals"]}/{expected}; invalidAnswerSets={invalidAnswerSets}");
        return passed ? 0 : 1;
    }
}

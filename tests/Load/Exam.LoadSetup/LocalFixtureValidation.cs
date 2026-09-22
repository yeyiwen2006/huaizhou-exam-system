using System.Text.Json;
using System.Text.RegularExpressions;
using Npgsql;

public static partial class LocalFixtureValidation
{
    public static async Task RequireFreshDatabaseAsync(string directory)
    {
        using var config = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "local-server.json")));
        await using var connection = new NpgsqlConnection(config.RootElement.GetProperty("ConnectionStrings").GetProperty("Exam").GetString());
        await connection.OpenAsync();
        await using var query = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM exam.participation_progress) OR EXISTS(SELECT 1 FROM exam.final_submissions)", connection);
        if ((bool)(await query.ExecuteScalarAsync())!)
            throw new InvalidOperationException("Fixture has already received answers. Create a new isolated fixture, never reset or reuse it silently.");
    }

    [GeneratedRegex("^exam_load_[a-f0-9]{32}$", RegexOptions.CultureInvariant)]
    private static partial Regex DatabaseName();

    public static void Validate(string directory)
    {
        using var setup = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "setup.json")));
        using var configuration = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "local-server.json")));
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "sessions.json")));
        var database = setup.RootElement.GetProperty("database").GetString()!;
        var examId = setup.RootElement.GetProperty("examId").GetString()!;
        var options = new NpgsqlConnectionStringBuilder(configuration.RootElement.GetProperty("ConnectionStrings").GetProperty("Exam").GetString());
        if (!setup.RootElement.GetProperty("synthetic").GetBoolean() || !DatabaseName().IsMatch(database) ||
            options.Database != database || options.Host is not ("127.0.0.1" or "localhost" or "::1"))
            throw new InvalidOperationException("Only matching loopback exam_load_* databases may be used.");
        if (!manifest.RootElement.GetProperty("synthetic").GetBoolean() ||
            manifest.RootElement.GetProperty("sessions").GetArrayLength() != setup.RootElement.GetProperty("count").GetInt32() ||
            manifest.RootElement.GetProperty("sessions").EnumerateArray().Any(s => s.GetProperty("examId").GetString() != examId))
            throw new InvalidOperationException("Synthetic manifest identities/count do not match the isolated fixture.");
        if (manifest.RootElement.GetProperty("serverId").GetString() != configuration.RootElement.GetProperty("ExamServer").GetProperty("ServerId").GetString() ||
            manifest.RootElement.GetProperty("serverEpoch").GetInt64() != configuration.RootElement.GetProperty("ExamServer").GetProperty("ServerEpoch").GetInt64())
            throw new InvalidOperationException("Manifest and server identities do not match.");
    }
}

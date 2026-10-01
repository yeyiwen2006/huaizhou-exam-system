using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Npgsql;

/// <summary>Ownership is bound to both a private random capability and a database-side marker.</summary>
internal static partial class FixtureOwnership
{
    [GeneratedRegex("^exam_load_[a-f0-9]{32}$", RegexOptions.CultureInvariant)]
    private static partial Regex Name();
    private sealed record Ownership(string Database, string Capability);

    public static async Task MarkAsync(string directory, string adminConnection, string database)
    {
        Directory.CreateDirectory(directory);
        var capability = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        await File.WriteAllTextAsync(Path.Combine(directory, "ownership.json"), JsonSerializer.Serialize(new Ownership(database, capability)));
        await using var admin = new NpgsqlConnection(adminConnection);
        await admin.OpenAsync();
        var marker = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(capability)));
        await using var command = new NpgsqlCommand($"COMMENT ON DATABASE \"{database}\" IS 'exam-synthetic-owner:{marker}'", admin);
        await command.ExecuteNonQueryAsync();
    }

    public static async Task<int> CleanupAsync(string directory)
    {
        LocalFixtureValidation.Validate(directory);
        var owner = JsonSerializer.Deserialize<Ownership>(await File.ReadAllTextAsync(Path.Combine(directory,"ownership.json")))
            ?? throw new InvalidOperationException("Private fixture ownership missing.");
        using var config = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory,"local-server.json")));
        var settings = new NpgsqlConnectionStringBuilder(config.RootElement.GetProperty("ConnectionStrings").GetProperty("Exam").GetString());
        if (!Name().IsMatch(owner.Database) || owner.Database != settings.Database || owner.Capability.Length != 64)
            throw new InvalidOperationException("Fixture ownership mismatch.");
        settings.Database = "postgres"; settings.Pooling = false;
        await using var admin = new NpgsqlConnection(settings.ConnectionString);
        await admin.OpenAsync();
        await using var marker = new NpgsqlCommand("SELECT shobj_description(oid,'pg_database') FROM pg_database WHERE datname=$1",admin);
        marker.Parameters.AddWithValue(owner.Database);
        var expected = "exam-synthetic-owner:"+Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(owner.Capability)));
        if (await marker.ExecuteScalarAsync() is not string actual || actual != expected)
            throw new InvalidOperationException("Database ownership marker does not match; cleanup refused.");
        await using var drop = new NpgsqlCommand($"DROP DATABASE \"{owner.Database}\" WITH (FORCE)",admin);
        await drop.ExecuteNonQueryAsync();
        Console.WriteLine("Owned synthetic database cleaned up.");
        return 0;
    }

    public static async Task DropCreatedAsync(string adminConnection, string database)
    {
        if (!Name().IsMatch(database)) throw new InvalidOperationException("Unsafe database name.");
        await using var admin = new NpgsqlConnection(adminConnection);
        await admin.OpenAsync();
        await using var drop = new NpgsqlCommand($"DROP DATABASE \"{database}\" WITH (FORCE)",admin);
        await drop.ExecuteNonQueryAsync();
    }
}

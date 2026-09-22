using System.Text.Json;

/// <summary>Server configuration uses its original property names, unlike the web session manifest.</summary>
public static class FixtureConfiguration
{
    public static async Task WriteAsync<T>(string path, T configuration, CancellationToken cancellationToken = default)
    {
        // CreateNew also prevents an accidental overwrite of existing private configuration.
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await JsonSerializer.SerializeAsync(stream, configuration,
            new JsonSerializerOptions { WriteIndented = true }, cancellationToken);
    }
}

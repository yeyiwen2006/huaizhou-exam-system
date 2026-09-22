using System.Text.Json;
using System.Text.Json.Nodes;

namespace Exam.UnitTests.Importing;

public sealed class SchemaConsistencyTests
{
    [Fact]
    public void StandaloneSchemas_MatchManifestEmbeddedDefinitions()
    {
        var root = FindRepositoryRoot();
        var manifest = LoadObject(Path.Combine(root, "schemas", "exam-manifest.schema.json"));
        var display = LoadObject(Path.Combine(root, "schemas", "display-metadata.schema.json"));
        var scoring = LoadObject(Path.Combine(root, "schemas", "scoring-rules.schema.json"));
        var manifestDefs = manifest["$defs"]!.AsObject();

        AssertEquivalent(Normalize(display), Normalize(manifestDefs["displayMetadata"]!), "display metadata");

        var scoringCore = scoring.DeepClone().AsObject();
        scoringCore.Remove("$defs");
        AssertEquivalent(Normalize(scoringCore), Normalize(manifestDefs["scoring"]!), "scoring root");

        foreach (var definition in scoring["$defs"]!.AsObject())
        {
            Assert.True(
                manifestDefs.TryGetPropertyValue(definition.Key, out var embedded) && embedded is not null,
                $"Manifest is missing scoring definition '{definition.Key}'.");
            AssertEquivalent(
                Normalize(definition.Value!),
                Normalize(embedded),
                $"scoring definition '{definition.Key}'");
        }
    }

    [Fact]
    public void SyntheticJsonFixtures_AreValidJson_AndErrorCatalogHas26Cases()
    {
        var root = FindRepositoryRoot();
        var paths = Directory.EnumerateFiles(Path.Combine(root, "schemas"), "*.json")
            .Concat(Directory.EnumerateFiles(Path.Combine(root, "samples", "synthetic"), "*.json", SearchOption.AllDirectories))
            .ToArray();

        Assert.NotEmpty(paths);
        foreach (var path in paths)
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            Assert.NotEqual(JsonValueKind.Undefined, document.RootElement.ValueKind);
        }

        var catalog = LoadObject(Path.Combine(root, "samples", "synthetic", "errors", "expected-errors.json"));
        Assert.Equal(26, catalog["cases"]!.AsArray().Count);
    }

    private static JsonObject LoadObject(string path) =>
        JsonNode.Parse(File.ReadAllText(path))?.AsObject()
        ?? throw new InvalidDataException($"JSON object expected: {path}");

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Huaizhou.ExamSystem.sln")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Repository root containing Huaizhou.ExamSystem.sln was not found.");
    }

    private static JsonNode Normalize(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            var normalized = new JsonObject();
            foreach (var property in obj)
            {
                if (property.Key is "$schema" or "$id" or "title" or "description")
                {
                    continue;
                }

                normalized[property.Key] = property.Value is null ? null : Normalize(property.Value);
            }

            return normalized;
        }

        if (node is JsonArray array)
        {
            var normalized = new JsonArray();
            foreach (var item in array)
            {
                normalized.Add(item is null ? null : Normalize(item));
            }

            return normalized;
        }

        return node.DeepClone();
    }

    private static void AssertEquivalent(JsonNode expected, JsonNode actual, string label) =>
        Assert.True(
            JsonNode.DeepEquals(expected, actual),
            $"{label} constraints drifted between standalone and embedded schemas.\nExpected: {expected}\nActual: {actual}");
}

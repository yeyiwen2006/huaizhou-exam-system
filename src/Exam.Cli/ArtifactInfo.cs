using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;

internal static partial class ExamCli
{
    private static int PrintArtifactInfo(string path)
    {
        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        if (!pe.HasMetadata) return 2;
        var reader = pe.GetMetadataReader();
        var assembly = reader.GetAssemblyDefinition();
        var metadata = new Dictionary<string,string>(StringComparer.Ordinal);
        string? information = null;
        foreach (var handle in assembly.GetCustomAttributes())
        {
            var attribute = reader.GetCustomAttribute(handle);
            if (attribute.Constructor.Kind != HandleKind.MemberReference) continue;
            var constructor = reader.GetMemberReference((MemberReferenceHandle)attribute.Constructor);
            if (constructor.Parent.Kind != HandleKind.TypeReference) continue;
            var type = reader.GetTypeReference((TypeReferenceHandle)constructor.Parent);
            if (reader.GetString(type.Namespace) != "System.Reflection") continue;
            var name = reader.GetString(type.Name);
            var value = reader.GetBlobReader(attribute.Value);
            if (value.ReadUInt16() != 1) return 2;
            if (name == "AssemblyInformationalVersionAttribute") information = value.ReadSerializedString();
            if (name == "AssemblyMetadataAttribute")
            {
                var key = value.ReadSerializedString(); var text = value.ReadSerializedString();
                if (key is not null && key.StartsWith("E01",StringComparison.Ordinal) && text is not null) metadata.Add(key,text);
            }
        }
        Console.WriteLine(JsonSerializer.Serialize(new { schemaVersion=1, assemblyName=reader.GetString(assembly.Name),
            version=assembly.Version.ToString(), informationalVersion=information, metadata }));
        return 0;
    }
}

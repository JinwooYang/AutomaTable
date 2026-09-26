using Mono.Cecil;

namespace AutomaTable.Tool.Schema;

internal static class SchemaMetadataReader
{
    public static SchemaManifest? TryRead(string assemblyPath)
    {
        using var assembly = AssemblyDefinition.ReadAssembly(assemblyPath, new ReaderParameters
        {
            ReadingMode = ReadingMode.Deferred,
            ReadSymbols = false
        });

        foreach (var attribute in assembly.CustomAttributes)
        {
            if (attribute.AttributeType.FullName != "System.Reflection.AssemblyMetadataAttribute" ||
                attribute.ConstructorArguments.Count != 2)
            {
                continue;
            }

            if (attribute.ConstructorArguments[0].Value is string key &&
                key == SchemaManifest.MetadataKey &&
                attribute.ConstructorArguments[1].Value is string json)
            {
                return SchemaManifest.Parse(json);
            }
        }

        return null;
    }
}

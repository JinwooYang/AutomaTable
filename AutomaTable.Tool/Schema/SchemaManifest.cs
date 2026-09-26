using System.Text.Json;
using System.Text.Json.Serialization;

namespace AutomaTable.Tool.Schema;

internal sealed class SchemaManifest
{
    public const string MetadataKey = "AutomaTable.Schema.v1";

    [JsonPropertyName("version")]
    public int Version { get; set; }

    [JsonPropertyName("tables")]
    public List<TableSchema> Tables { get; set; } = new();

    public static SchemaManifest Parse(string json)
    {
        var manifest = JsonSerializer.Deserialize<SchemaManifest>(json, JsonOptions)
            ?? throw new InvalidDataException("The AutomaTable schema metadata is empty.");
        if (manifest.Version != 1)
            throw new InvalidDataException($"Unsupported AutomaTable schema version: {manifest.Version}.");
        if (manifest.Tables.Count == 0)
            throw new InvalidDataException("The AutomaTable schema does not contain any tables.");

        manifest.Validate();
        return manifest;
    }

    private void Validate()
    {
        var tableNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var table in Tables)
        {
            if (string.IsNullOrWhiteSpace(table.Name) || !tableNames.Add(table.Name))
                throw new InvalidDataException($"The schema contains an invalid or duplicate table name '{table.Name}'.");
            if (table.Columns.Count == 0)
                throw new InvalidDataException($"Table '{table.Name}' does not contain any columns.");

            var columnNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var column in table.Columns)
            {
                if (string.IsNullOrWhiteSpace(column.Name) || !columnNames.Add(column.Name))
                    throw new InvalidDataException($"Table '{table.Name}' contains an invalid or duplicate column '{column.Name}'.");
            }

            var id = table.Columns.SingleOrDefault(static column => column.Name == "Id");
            if (id == null || id.Storage != "Integer" || id.Nullable)
                throw new InvalidDataException($"Table '{table.Name}' must contain a required integer Id column.");
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false
    };
}

internal sealed class TableSchema
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("columns")]
    public List<ColumnSchema> Columns { get; set; } = new();

    [JsonPropertyName("indexes")]
    public List<IndexSchema> Indexes { get; set; } = new();
}

internal sealed class ColumnSchema
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("storage")]
    public string Storage { get; set; } = string.Empty;

    [JsonPropertyName("kind")]
    public string Kind { get; set; } = string.Empty;

    [JsonPropertyName("nullable")]
    public bool Nullable { get; set; }

    [JsonPropertyName("referenceTable")]
    public string? ReferenceTable { get; set; }

    [JsonPropertyName("enumName")]
    public string? EnumName { get; set; }

    [JsonPropertyName("enumValues")]
    public Dictionary<string, long>? EnumValues { get; set; }
}

internal sealed class IndexSchema
{
    [JsonPropertyName("unique")]
    public bool Unique { get; set; }

    [JsonPropertyName("columns")]
    public List<string> Columns { get; set; } = new();
}

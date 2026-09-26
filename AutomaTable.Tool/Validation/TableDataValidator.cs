using AutomaTable.Tool.Schema;
using SQLite;

namespace AutomaTable.Tool.Validation;

internal sealed record ValidationCheck(string Subject, string Rule, string? Failure);

internal sealed class ValidationResult
{
    public ValidationResult(IReadOnlyList<ValidationCheck> checks)
    {
        Checks = checks;
    }

    public IReadOnlyList<ValidationCheck> Checks { get; }
    public bool Succeeded => Checks.All(static check => check.Failure == null);
}

internal static class TableDataValidator
{
    private const int FailureLimit = 50;

    public static ValidationResult Validate(SchemaManifest manifest, string databasePath, string? resourcesPath)
    {
        var fullDatabasePath = Path.GetFullPath(databasePath);
        if (!File.Exists(fullDatabasePath))
            throw new FileNotFoundException("The generated AutomaTable database does not exist.", fullDatabasePath);

        var checks = new List<ValidationCheck>();
        using var connection = new SQLiteConnection(
            fullDatabasePath,
            SQLiteOpenFlags.ReadOnly | SQLiteOpenFlags.FullMutex);

        foreach (var table in manifest.Tables)
        {
            ValidateTableShape(connection, table, checks);
            ValidateUnique(connection, table, new[] { "Id" }, checks);
            foreach (var index in table.Indexes.Where(static value => value.Unique))
                ValidateUnique(connection, table, index.Columns, checks);

            foreach (var column in table.Columns.Where(static value =>
                         value.Kind == "Id" && value.Name != "Id" && value.ReferenceTable != null))
            {
                ValidateReference(connection, table, column, checks);
            }
        }

        ValidateAssets(connection, manifest, resourcesPath, checks);
        return new ValidationResult(checks);
    }

    private static void ValidateTableShape(SQLiteConnection connection, TableSchema table, List<ValidationCheck> checks)
    {
        var rows = connection.Query<PragmaTableInfoRow>("PRAGMA table_info(" + QuoteIdentifier(table.Name) + ")");
        var actualColumns = rows.Select(static row => row.Name).ToHashSet(StringComparer.Ordinal);
        var missing = table.Columns.Select(static value => value.Name).Where(value => !actualColumns.Contains(value)).ToArray();
        checks.Add(new ValidationCheck(
            table.Name,
            "Schema",
            rows.Count == 0
                ? $"Table '{table.Name}' does not exist."
                : missing.Length == 0 ? null : "Missing columns: " + string.Join(", ", missing)));
    }

    private static void ValidateUnique(
        SQLiteConnection connection,
        TableSchema table,
        IReadOnlyList<string> columns,
        List<ValidationCheck> checks)
    {
        var groupColumns = string.Join(", ", columns.Select(QuoteIdentifier));
        var sql = "SELECT CAST(MIN(" + QuoteIdentifier("Id") + ") AS TEXT) AS Value " +
                  "FROM " + QuoteIdentifier(table.Name) + " " +
                  "GROUP BY " + groupColumns + " HAVING COUNT(*) > 1 LIMIT " + FailureLimit;
        var duplicates = connection.Query<ScalarTextRow>(sql).Select(static row => row.Value).ToArray();
        checks.Add(new ValidationCheck(
            table.Name + "." + string.Join("+", columns),
            "Unique",
            duplicates.Length == 0
                ? null
                : "Representative duplicate row Ids: " + string.Join(", ", duplicates)));
    }

    private static void ValidateReference(
        SQLiteConnection connection,
        TableSchema sourceTable,
        ColumnSchema column,
        List<ValidationCheck> checks)
    {
        var targetName = column.ReferenceTable!;
        var sourceReference = "source." + QuoteIdentifier(column.Name);
        var invalidPredicate = column.Nullable
            ? sourceReference + " IS NOT NULL AND target." + QuoteIdentifier("Id") + " IS NULL"
            : sourceReference + " IS NULL OR target." + QuoteIdentifier("Id") + " IS NULL";
        var sql = "SELECT CAST(source." + QuoteIdentifier("Id") + " AS TEXT) AS RowId, " +
                  "CAST(" + sourceReference + " AS TEXT) AS Value " +
                  "FROM " + QuoteIdentifier(sourceTable.Name) + " AS source " +
                  "LEFT JOIN " + QuoteIdentifier(targetName) + " AS target ON target." +
                  QuoteIdentifier("Id") + " = " + sourceReference + " " +
                  "WHERE " + invalidPredicate + " LIMIT " + FailureLimit;
        var failures = connection.Query<ValidationValueRow>(sql);
        checks.Add(new ValidationCheck(
            sourceTable.Name + "." + column.Name,
            "Reference -> " + targetName,
            failures.Count == 0 ? null : BuildFailureMessage(failures)));
    }

    private static void ValidateAssets(
        SQLiteConnection connection,
        SchemaManifest manifest,
        string? resourcesPath,
        List<ValidationCheck> checks)
    {
        var assetColumns = manifest.Tables
            .SelectMany(static table => table.Columns
                .Where(static column => column.Kind == "AssetAddress")
                .Select(column => (Table: table, Column: column)))
            .ToArray();
        if (assetColumns.Length == 0)
            return;

        if (string.IsNullOrWhiteSpace(resourcesPath))
        {
            foreach (var pair in assetColumns)
            {
                checks.Add(new ValidationCheck(
                    pair.Table.Name + "." + pair.Column.Name,
                    "Asset",
                    "A resources path is required to validate AssetAddress values."));
            }
            return;
        }

        var fullResourcesPath = Path.GetFullPath(resourcesPath);
        if (!Directory.Exists(fullResourcesPath))
        {
            foreach (var pair in assetColumns)
            {
                checks.Add(new ValidationCheck(
                    pair.Table.Name + "." + pair.Column.Name,
                    "Asset",
                    "Resources directory does not exist: " + fullResourcesPath));
            }
            return;
        }

        var resources = Directory.EnumerateFiles(fullResourcesPath, "*", SearchOption.AllDirectories)
            .Where(static path => !string.Equals(Path.GetExtension(path), ".meta", StringComparison.OrdinalIgnoreCase))
            .Select(path => Path.GetRelativePath(fullResourcesPath, path).Replace(Path.DirectorySeparatorChar, '/'))
            .ToHashSet(StringComparer.Ordinal);

        foreach (var pair in assetColumns)
        {
            var sql = "SELECT CAST(" + QuoteIdentifier("Id") + " AS TEXT) AS RowId, " +
                      QuoteIdentifier(pair.Column.Name) + " AS Value FROM " + QuoteIdentifier(pair.Table.Name);
            var failures = new List<ValidationValueRow>();
            foreach (var row in connection.Query<ValidationValueRow>(sql))
            {
                if (row.Value == null)
                {
                    if (!pair.Column.Nullable) failures.Add(row);
                }
                else if (!IsValidAssetAddress(row.Value) || !resources.Contains(row.Value))
                {
                    failures.Add(row);
                }

                if (failures.Count == FailureLimit)
                    break;
            }

            checks.Add(new ValidationCheck(
                pair.Table.Name + "." + pair.Column.Name,
                "Asset",
                failures.Count == 0 ? null : BuildFailureMessage(failures)));
        }
    }

    private static bool IsValidAssetAddress(string address)
    {
        return !string.IsNullOrWhiteSpace(address) &&
               !Path.IsPathRooted(address) &&
               address.IndexOf('\\') < 0 &&
               address.Split('/').All(static segment => segment.Length > 0 && segment != "." && segment != "..") &&
               !string.IsNullOrEmpty(Path.GetExtension(address));
    }

    private static string BuildFailureMessage(IReadOnlyList<ValidationValueRow> failures) =>
        string.Join(Environment.NewLine, failures.Select(static value =>
            "Row Id=" + value.RowId + ", Value=" + (value.Value ?? "NULL")));

    private static string QuoteIdentifier(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";

    private sealed class PragmaTableInfoRow
    {
        public string Name { get; set; } = string.Empty;
    }

    private sealed class ScalarTextRow
    {
        public string Value { get; set; } = string.Empty;
    }

    private sealed class ValidationValueRow
    {
        public string RowId { get; set; } = string.Empty;
        public string? Value { get; set; }
    }
}

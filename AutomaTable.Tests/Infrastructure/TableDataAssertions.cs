using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using SQLite;

namespace AutomaTable.Tests.Infrastructure
{
    internal static class TableDataAssertions
    {
        private const int FailureLimit = 50;

        public static void AssertUnique(string tableName, IReadOnlyList<string> columns)
        {
            using var connection = OpenDatabase();
            var groupColumns = string.Join(", ", columns.Select(QuoteIdentifier));
            var sql = "SELECT MIN(" + QuoteIdentifier("Id") + ") " +
                      "FROM " + QuoteIdentifier(tableName) + " " +
                      "GROUP BY " + groupColumns + " HAVING COUNT(*) > 1 LIMIT " + FailureLimit;
            var duplicateRowIds = connection.QueryScalars<long>(sql);

            Assert.That(
                duplicateRowIds,
                Is.Empty,
                tableName + "." + string.Join("+", columns) +
                " must be unique. Representative duplicate row Ids: " + string.Join(", ", duplicateRowIds));
        }

        public static void AssertReferenceExists(
            string sourceTable,
            string sourceIdColumn,
            string referenceColumn,
            string targetTable,
            string targetIdColumn,
            bool allowNull)
        {
            using var connection = OpenDatabase();
            var sourceReference = "source." + QuoteIdentifier(referenceColumn);
            var invalidPredicate = allowNull
                ? sourceReference + " IS NOT NULL AND target." + QuoteIdentifier(targetIdColumn) + " IS NULL"
                : sourceReference + " IS NULL OR target." + QuoteIdentifier(targetIdColumn) + " IS NULL";
            var sql = "SELECT CAST(source." + QuoteIdentifier(sourceIdColumn) + " AS TEXT) AS RowId, " +
                      "CAST(" + sourceReference + " AS TEXT) AS Value " +
                      "FROM " + QuoteIdentifier(sourceTable) + " AS source " +
                      "LEFT JOIN " + QuoteIdentifier(targetTable) + " AS target ON target." +
                      QuoteIdentifier(targetIdColumn) + " = " + sourceReference + " " +
                      "WHERE " + invalidPredicate + " LIMIT " + FailureLimit;
            var failures = connection.Query<ValidationValueRow>(sql);

            Assert.That(
                failures,
                Is.Empty,
                BuildFailureMessage(
                    sourceTable + "." + referenceColumn + " contains missing " + targetTable + " references.",
                    failures));
        }

        public static void AssertAssetAddressExists(
            string tableName,
            string rowIdColumn,
            string addressColumn,
            bool allowNull)
        {
            var resourcesPath = TableDataTestEnvironment.ResourcesPath;
            Assert.That(Directory.Exists(resourcesPath), Is.True, "Resources directory does not exist: " + resourcesPath);

            var resourceAddresses = new HashSet<string>(StringComparer.Ordinal);
            foreach (var file in Directory.EnumerateFiles(resourcesPath, "*", SearchOption.AllDirectories))
            {
                if (string.Equals(Path.GetExtension(file), ".meta", StringComparison.OrdinalIgnoreCase))
                    continue;

                var relative = file.Substring(resourcesPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Length)
                    .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    .Replace(Path.DirectorySeparatorChar, '/');
                resourceAddresses.Add(relative);
            }

            using var connection = OpenDatabase();
            var sql = "SELECT CAST(" + QuoteIdentifier(rowIdColumn) + " AS TEXT) AS RowId, " +
                      QuoteIdentifier(addressColumn) + " AS Value FROM " + QuoteIdentifier(tableName);
            var rows = connection.Query<ValidationValueRow>(sql);
            var failures = new List<ValidationValueRow>();
            foreach (var row in rows)
            {
                if (row.Value == null)
                {
                    if (!allowNull) failures.Add(row);
                    continue;
                }

                if (!IsValidAssetAddress(row.Value) || !resourceAddresses.Contains(row.Value))
                    failures.Add(row);

                if (failures.Count == FailureLimit)
                    break;
            }

            Assert.That(
                failures,
                Is.Empty,
                BuildFailureMessage(
                    tableName + "." + addressColumn +
                    " contains invalid or missing asset addresses. Addresses must include the file extension and be relative to: " +
                    resourcesPath,
                    failures));
        }

        private static SQLiteConnection OpenDatabase()
        {
            var databasePath = TableDataTestEnvironment.DatabasePath;
            Assert.That(File.Exists(databasePath), Is.True, "Table database does not exist: " + databasePath);
            return new SQLiteConnection(databasePath, SQLiteOpenFlags.ReadOnly | SQLiteOpenFlags.FullMutex);
        }

        private static bool IsValidAssetAddress(string address)
        {
            return !string.IsNullOrWhiteSpace(address) &&
                   !Path.IsPathRooted(address) &&
                   address.IndexOf('\\') < 0 &&
                   address.Split('/').All(segment => segment.Length > 0 && segment != "." && segment != "..") &&
                   !string.IsNullOrEmpty(Path.GetExtension(address));
        }

        private static string BuildFailureMessage(string heading, IReadOnlyList<ValidationValueRow> failures)
        {
            return heading + Environment.NewLine + string.Join(
                Environment.NewLine,
                failures.Select(value => "Row Id=" + value.RowId + ", Value=" + (value.Value ?? "NULL")));
        }

        private static string QuoteIdentifier(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";

        private sealed class ValidationValueRow
        {
            public string RowId { get; set; } = string.Empty;
            public string? Value { get; set; }
        }
    }
}

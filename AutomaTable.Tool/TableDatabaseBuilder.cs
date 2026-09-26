using System.Collections.ObjectModel;
using AutomaTable.Tool.Runtime;
using AutomaTable.Tool.Schema;
using SQLite;

namespace AutomaTable.Tool;

internal static class TableDatabaseBuilder
{
    public static ImportResult Build(
        SchemaManifest manifest,
        string xlsxDirectory,
        string outputDatabasePath)
    {
        if (string.IsNullOrWhiteSpace(outputDatabasePath))
            throw new ArgumentException("An output database path is required.", nameof(outputDatabasePath));

        var catalog = WorkbookCatalog.Scan(xlsxDirectory);
        foreach (var table in manifest.Tables)
            catalog.GetRequiredWorkbook(table.Name);

        var outputPath = Path.GetFullPath(outputDatabasePath);
        var outputDirectory = Path.GetDirectoryName(outputPath)
            ?? throw new InvalidDataException($"Output path '{outputPath}' has no parent directory.");
        Directory.CreateDirectory(outputDirectory);

        var temporaryPath = Path.Combine(
            outputDirectory,
            $".{Path.GetFileName(outputPath)}.{Guid.NewGuid():N}.tmp");
        var tableResults = new List<TableImportResult>(manifest.Tables.Count);
        SQLiteConnection? connection = null;
        var transactionStarted = false;
        try
        {
            connection = new SQLiteConnection(
                temporaryPath,
                SQLiteOpenFlags.ReadWrite | SQLiteOpenFlags.Create | SQLiteOpenFlags.FullMutex);
            connection.Execute("PRAGMA foreign_keys = ON");
            connection.BeginTransaction();
            transactionStarted = true;

            foreach (var table in manifest.Tables)
                connection.Execute(BuildCreateTableSql(table));

            foreach (var table in manifest.Tables)
            {
                var workbookPath = catalog.GetRequiredWorkbook(table.Name);
                using var reader = catalog.OpenRequiredSheet(table.Name);
                var rowCount = ImportRows(table, reader, connection);
                tableResults.Add(new TableImportResult(table.Name, workbookPath, rowCount));
            }

            foreach (var table in manifest.Tables)
            {
                foreach (var index in table.Indexes)
                    connection.Execute(BuildCreateIndexSql(table, index));
            }

            connection.Commit();
            transactionStarted = false;
            connection.Close();
            connection.Dispose();
            connection = null;

            File.Move(temporaryPath, outputPath, true);
            return new ImportResult(
                outputPath,
                catalog.WorkbookCount,
                new ReadOnlyCollection<TableImportResult>(tableResults));
        }
        catch
        {
            if (transactionStarted && connection != null)
            {
                try
                {
                    connection.Rollback();
                }
                catch
                {
                    // Preserve the original build failure.
                }
            }

            throw;
        }
        finally
        {
            connection?.Dispose();
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private static int ImportRows(TableSchema table, ExcelDataReader.IExcelDataReader reader, SQLiteConnection connection)
    {
        var columns = ExcelImportRuntime.ReadHeader(reader, table.Name, table.Columns.Select(static value => value.Name).ToArray());
        var statement = SqliteWriteRuntime.Prepare(connection.Handle, BuildInsertSql(table));
        var importedRowCount = 0;
        var rowNumber = 1;
        try
        {
            while (reader.Read())
            {
                rowNumber++;
                if (ExcelImportRuntime.IsBlankRow(reader, columns))
                    continue;

                for (var index = 0; index < table.Columns.Count; index++)
                {
                    var column = table.Columns[index];
                    var value = reader.GetValue(columns[index]);
                    BindValue(connection, statement, index + 1, table.Name, column, rowNumber, value);
                }

                SqliteWriteRuntime.Step(connection.Handle, statement);
                SqliteWriteRuntime.Reset(connection.Handle, statement);
                importedRowCount++;
            }
        }
        finally
        {
            SqliteWriteRuntime.Finalize(statement);
        }

        return importedRowCount;
    }

    private static void BindValue(
        SQLiteConnection connection,
        SQLitePCL.sqlite3_stmt statement,
        int parameterIndex,
        string tableName,
        ColumnSchema column,
        int rowNumber,
        object? value)
    {
        switch (column.Kind)
        {
            case "Enum":
                SqliteWriteRuntime.BindInt64(
                    connection.Handle,
                    statement,
                    parameterIndex,
                    ExcelImportRuntime.ReadEnum(value, tableName, column, rowNumber));
                return;
            case "Boolean":
                SqliteWriteRuntime.BindInt64(
                    connection.Handle,
                    statement,
                    parameterIndex,
                    ExcelImportRuntime.ReadBoolean(value, tableName, column.Name, rowNumber) ? 1L : 0L);
                return;
            case "Real":
                SqliteWriteRuntime.BindDouble(
                    connection.Handle,
                    statement,
                    parameterIndex,
                    ExcelImportRuntime.ReadDouble(value, tableName, column.Name, rowNumber));
                return;
            case "String":
            case "AssetAddress":
                SqliteWriteRuntime.BindText(
                    connection.Handle,
                    statement,
                    parameterIndex,
                    column.Nullable
                        ? ExcelImportRuntime.ReadNullableString(value, tableName, column.Name, rowNumber)
                        : ExcelImportRuntime.ReadString(value, tableName, column.Name, rowNumber));
                return;
            case "Blob":
                if (column.Nullable)
                {
                    SqliteWriteRuntime.BindNullableBlob(
                        connection.Handle,
                        statement,
                        parameterIndex,
                        ExcelImportRuntime.ReadNullableBlob(value, tableName, column.Name, rowNumber));
                }
                else
                {
                    SqliteWriteRuntime.BindBlob(
                        connection.Handle,
                        statement,
                        parameterIndex,
                        ExcelImportRuntime.ReadBlob(value, tableName, column.Name, rowNumber));
                }
                return;
            case "DateTime":
                SqliteWriteRuntime.BindInt64(connection.Handle, statement, parameterIndex,
                    ExcelImportRuntime.ReadDateTimeTicks(value, tableName, column.Name, rowNumber));
                return;
            case "DateTimeOffset":
                SqliteWriteRuntime.BindInt64(connection.Handle, statement, parameterIndex,
                    ExcelImportRuntime.ReadDateTimeOffsetTicks(value, tableName, column.Name, rowNumber));
                return;
            case "TimeSpan":
                SqliteWriteRuntime.BindInt64(connection.Handle, statement, parameterIndex,
                    ExcelImportRuntime.ReadTimeSpanTicks(value, tableName, column.Name, rowNumber));
                return;
            case "Guid":
                SqliteWriteRuntime.BindText(connection.Handle, statement, parameterIndex,
                    ExcelImportRuntime.ReadGuid(value, tableName, column.Name, rowNumber));
                return;
            default:
                if (column.Nullable)
                {
                    SqliteWriteRuntime.BindNullableInt64(connection.Handle, statement, parameterIndex,
                        ExcelImportRuntime.ReadNullableInt64(value, tableName, column.Name, rowNumber));
                }
                else
                {
                    SqliteWriteRuntime.BindInt64(connection.Handle, statement, parameterIndex,
                        ExcelImportRuntime.ReadInt64(value, tableName, column.Name, rowNumber));
                }
                return;
        }
    }

    private static string BuildCreateTableSql(TableSchema table)
    {
        var definitions = table.Columns.Select(column =>
        {
            if (column.Name == "Id")
                return QuoteIdentifier(column.Name) + " INTEGER PRIMARY KEY";
            var sqlType = column.Storage switch
            {
                "Integer" => "INTEGER",
                "Real" => "REAL",
                "Text" => "TEXT",
                "Blob" => "BLOB",
                _ => throw new InvalidDataException(
                    $"Table '{table.Name}', column '{column.Name}' has unsupported storage '{column.Storage}'.")
            };
            return QuoteIdentifier(column.Name) + " " + sqlType + (column.Nullable ? string.Empty : " NOT NULL");
        });
        return "CREATE TABLE " + QuoteIdentifier(table.Name) + " (" + string.Join(", ", definitions) + ")";
    }

    private static string BuildCreateIndexSql(TableSchema table, IndexSchema index)
    {
        var name = "IX_" + table.Name + "_" + string.Join("_", index.Columns);
        return "CREATE " + (index.Unique ? "UNIQUE " : string.Empty) + "INDEX " + QuoteIdentifier(name) +
               " ON " + QuoteIdentifier(table.Name) + " (" +
               string.Join(", ", index.Columns.Select(QuoteIdentifier)) + ")";
    }

    private static string BuildInsertSql(TableSchema table) =>
        "INSERT INTO " + QuoteIdentifier(table.Name) + " (" +
        string.Join(", ", table.Columns.Select(static column => QuoteIdentifier(column.Name))) + ") VALUES (" +
        string.Join(", ", Enumerable.Range(1, table.Columns.Count).Select(static value => "?" + value)) + ")";

    private static string QuoteIdentifier(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";
}

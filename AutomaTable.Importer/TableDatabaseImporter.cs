using System.Collections.ObjectModel;
using System.IO;
using AutomaTable.Importer.Generated;
using AutomaTable.Importer.Runtime;
using SQLite;

namespace AutomaTable.Importer
{
    public static class TableDatabaseImporter
    {
        public static ImportResult Import(string xlsxDirectory, string outputDatabasePath)
        {
            if (string.IsNullOrWhiteSpace(outputDatabasePath))
                throw new ArgumentException("An output database path is required.", nameof(outputDatabasePath));

            var catalog = WorkbookCatalog.Scan(xlsxDirectory);
            var importers = GeneratedTableImporterCatalog.All;
            foreach (var importer in importers)
                catalog.GetRequiredWorkbook(importer.TableName);

            var outputPath = Path.GetFullPath(outputDatabasePath);
            var outputDirectory = Path.GetDirectoryName(outputPath)
                ?? throw new InvalidDataException($"Output path '{outputPath}' has no parent directory.");
            Directory.CreateDirectory(outputDirectory);

            var temporaryPath = Path.Combine(
                outputDirectory,
                $".{Path.GetFileName(outputPath)}.{Guid.NewGuid():N}.tmp");
            var tableResults = new List<TableImportResult>(importers.Count);
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

                foreach (var importer in importers)
                    connection.Execute(importer.CreateTableSql);

                foreach (var importer in importers)
                {
                    var workbookPath = catalog.GetRequiredWorkbook(importer.TableName);
                    using var reader = catalog.OpenRequiredSheet(importer.TableName);
                    var rowCount = importer.ImportRows(reader, connection);
                    tableResults.Add(new TableImportResult(importer.TableName, workbookPath, rowCount));
                }

                foreach (var importer in importers)
                {
                    foreach (var createIndexSql in importer.CreateIndexSql)
                        connection.Execute(createIndexSql);
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
                        // Preserve the original import failure.
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
    }
}

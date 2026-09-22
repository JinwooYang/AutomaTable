using System.Collections.Generic;
using System.Linq;

namespace DataTable.Importer
{
    public sealed class ImportResult
    {
        internal ImportResult(string databasePath, int workbookCount, IReadOnlyList<TableImportResult> tables)
        {
            DatabasePath = databasePath;
            WorkbookCount = workbookCount;
            Tables = tables;
        }

        public string DatabasePath { get; }
        public int WorkbookCount { get; }
        public IReadOnlyList<TableImportResult> Tables { get; }
        public int TotalRowCount => Tables.Sum(static table => table.RowCount);
    }

    public sealed class TableImportResult
    {
        internal TableImportResult(string tableName, string workbookPath, int rowCount)
        {
            TableName = tableName;
            WorkbookPath = workbookPath;
            RowCount = rowCount;
        }

        public string TableName { get; }
        public string WorkbookPath { get; }
        public int RowCount { get; }
    }
}

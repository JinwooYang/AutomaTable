using ExcelDataReader;
using SQLite;

namespace AutomaTable.Importer.Runtime
{
    internal interface ITableSheetImporter
    {
        string TableName { get; }
        string CreateTableSql { get; }
        IReadOnlyList<string> CreateIndexSql { get; }
        int ImportRows(IExcelDataReader reader, SQLiteConnection connection);
    }
}

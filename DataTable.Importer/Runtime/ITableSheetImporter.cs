using ExcelDataReader;
using SQLite;

namespace DataTable.Importer.Runtime
{
    internal interface ITableSheetImporter
    {
        string TableName { get; }
        string CreateTableSql { get; }
        IReadOnlyList<string> CreateIndexSql { get; }
        int ImportRows(IExcelDataReader reader, SQLiteConnection connection);
    }
}

using AutomaTable.Importer;

if (args.Length != 2)
{
    Console.Error.WriteLine("Usage: AutomaTable.Importer <xlsx-directory> <output-database-path>");
    return 1;
}

try
{
    var result = TableDatabaseImporter.Import(args[0], args[1]);
    Console.WriteLine($"Imported {result.TotalRowCount} rows from {result.WorkbookCount} workbook(s) into '{result.DatabasePath}'.");
    foreach (var table in result.Tables)
        Console.WriteLine($"  {table.TableName}: {table.RowCount} row(s)");
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception.Message);
    return 2;
}

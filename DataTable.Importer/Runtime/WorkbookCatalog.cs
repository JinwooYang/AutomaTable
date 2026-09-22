using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using ExcelDataReader;

namespace DataTable.Importer.Runtime
{
    internal sealed class WorkbookCatalog
    {
        private readonly IReadOnlyDictionary<string, string> _sheets;

        private WorkbookCatalog(IReadOnlyDictionary<string, string> sheets, int workbookCount)
        {
            _sheets = sheets;
            WorkbookCount = workbookCount;
        }

        public int WorkbookCount { get; }

        public static WorkbookCatalog Scan(string inputDirectory)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

            if (string.IsNullOrWhiteSpace(inputDirectory))
                throw new ArgumentException("An XLSX input directory is required.", nameof(inputDirectory));

            var directory = Path.GetFullPath(inputDirectory);
            if (!Directory.Exists(directory))
                throw new DirectoryNotFoundException($"XLSX input directory does not exist: '{directory}'.");

            var workbookPaths = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                .Where(static path => string.Equals(Path.GetExtension(path), ".xlsx", StringComparison.OrdinalIgnoreCase))
                .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (workbookPaths.Length == 0)
                throw new InvalidDataException($"No .xlsx files were found under '{directory}'.");

            var sheets = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var workbookPath in workbookPaths)
            {
                using var stream = File.Open(workbookPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = ExcelReaderFactory.CreateReader(stream);
                do
                {
                    var sheetName = reader.Name;
                    if (string.IsNullOrWhiteSpace(sheetName))
                        throw new InvalidDataException($"Workbook '{workbookPath}' contains a sheet without a name.");

                    if (sheets.TryGetValue(sheetName, out var existingWorkbook))
                    {
                        throw new InvalidDataException(
                            $"Worksheet '{sheetName}' is duplicated in '{existingWorkbook}' and '{workbookPath}'. " +
                            "Each worksheet name must be unique across the input directory.");
                    }

                    sheets.Add(sheetName, workbookPath);
                } while (reader.NextResult());
            }

            return new WorkbookCatalog(
                new ReadOnlyDictionary<string, string>(sheets),
                workbookPaths.Length);
        }

        public string GetRequiredWorkbook(string sheetName)
        {
            if (_sheets.TryGetValue(sheetName, out var workbookPath))
                return workbookPath;

            throw new InvalidDataException(
                $"Required worksheet '{sheetName}' was not found in any .xlsx file under the input directory.");
        }

        public IExcelDataReader OpenRequiredSheet(string sheetName)
        {
            var workbookPath = GetRequiredWorkbook(sheetName);
            var stream = File.Open(workbookPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            try
            {
                var reader = ExcelReaderFactory.CreateReader(stream);
                do
                {
                    if (string.Equals(reader.Name, sheetName, StringComparison.Ordinal))
                        return reader;
                } while (reader.NextResult());

                reader.Dispose();
                throw new InvalidDataException(
                    $"Worksheet '{sheetName}' disappeared from workbook '{workbookPath}' after catalog scan.");
            }
            catch
            {
                stream.Dispose();
                throw;
            }
        }
    }
}

using System.Globalization;
using System.IO;
using ExcelDataReader;

namespace DataTable.Importer.Runtime
{
    internal static class ExcelImportRuntime
    {
        public static int[] ReadHeader(IExcelDataReader reader, string tableName, IReadOnlyList<string> expectedColumns)
        {
            if (!reader.Read())
                throw new InvalidDataException($"Worksheet '{tableName}' is empty; row 1 must contain column names.");

            var actualColumns = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var columnIndex = 0; columnIndex < reader.FieldCount; columnIndex++)
            {
                var value = reader.GetValue(columnIndex);
                if (value == null || value == DBNull.Value)
                    continue;

                var name = Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim();
                if (string.IsNullOrEmpty(name))
                    continue;

                if (!actualColumns.TryAdd(name, columnIndex))
                    throw new InvalidDataException($"Worksheet '{tableName}' contains duplicate column '{name}' in row 1.");
            }

            var expected = new HashSet<string>(expectedColumns, StringComparer.Ordinal);
            foreach (var actualColumn in actualColumns.Keys)
            {
                if (!expected.Contains(actualColumn))
                    throw new InvalidDataException($"Worksheet '{tableName}' contains unknown column '{actualColumn}'.");
            }

            var indexes = new int[expectedColumns.Count];
            for (var i = 0; i < expectedColumns.Count; i++)
            {
                if (!actualColumns.TryGetValue(expectedColumns[i], out indexes[i]))
                    throw new InvalidDataException($"Worksheet '{tableName}' is missing required column '{expectedColumns[i]}'.");
            }

            return indexes;
        }

        public static bool IsBlankRow(IExcelDataReader reader, IReadOnlyList<int> columnIndexes)
        {
            for (var i = 0; i < columnIndexes.Count; i++)
            {
                var value = reader.GetValue(columnIndexes[i]);
                if (value != null && value != DBNull.Value &&
                    (value is not string text || !string.IsNullOrWhiteSpace(text)))
                {
                    return false;
                }
            }

            return true;
        }

        public static long ReadInt64(object? value, string tableName, string columnName, int rowNumber)
        {
            if (IsNull(value))
                throw RequiredValue(tableName, columnName, rowNumber);

            try
            {
                if (value is double doubleValue)
                {
                    if (!double.IsFinite(doubleValue) || doubleValue != Math.Truncate(doubleValue))
                        throw new FormatException();
                    return checked((long)doubleValue);
                }

                if (value is float floatValue)
                {
                    if (!float.IsFinite(floatValue) || floatValue != MathF.Truncate(floatValue))
                        throw new FormatException();
                    return checked((long)floatValue);
                }

                if (value is string text)
                    return long.Parse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture);

                return Convert.ToInt64(value, CultureInfo.InvariantCulture);
            }
            catch (Exception exception) when (exception is FormatException or InvalidCastException or OverflowException)
            {
                throw InvalidValue(tableName, columnName, rowNumber, value, "a 64-bit integer");
            }
        }

        public static long? ReadNullableInt64(object? value, string tableName, string columnName, int rowNumber) =>
            IsNull(value) ? null : ReadInt64(value, tableName, columnName, rowNumber);

        public static double ReadDouble(object? value, string tableName, string columnName, int rowNumber)
        {
            if (IsNull(value))
                throw RequiredValue(tableName, columnName, rowNumber);

            try
            {
                var result = value is string text
                    ? double.Parse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture)
                    : Convert.ToDouble(value, CultureInfo.InvariantCulture);
                if (!double.IsFinite(result))
                    throw new FormatException();
                return result;
            }
            catch (Exception exception) when (exception is FormatException or InvalidCastException or OverflowException)
            {
                throw InvalidValue(tableName, columnName, rowNumber, value, "a finite number");
            }
        }

        public static string ReadString(object? value, string tableName, string columnName, int rowNumber)
        {
            if (IsNull(value))
                throw RequiredValue(tableName, columnName, rowNumber);

            var result = Convert.ToString(value, CultureInfo.InvariantCulture);
            if (string.IsNullOrEmpty(result))
                throw RequiredValue(tableName, columnName, rowNumber);
            return result;
        }

        public static string? ReadNullableString(object? value, string tableName, string columnName, int rowNumber) =>
            IsNull(value) ? null : ReadString(value, tableName, columnName, rowNumber);

        public static ReadOnlyMemory<byte> ReadBlob(object? value, string tableName, string columnName, int rowNumber)
        {
            var text = ReadString(value, tableName, columnName, rowNumber);
            try
            {
                return Convert.FromBase64String(text);
            }
            catch (FormatException)
            {
                throw InvalidValue(tableName, columnName, rowNumber, value, "Base64 text");
            }
        }

        public static ReadOnlyMemory<byte>? ReadNullableBlob(object? value, string tableName, string columnName, int rowNumber) =>
            IsNull(value) ? null : ReadBlob(value, tableName, columnName, rowNumber);

        public static long ReadDateTimeTicks(object? value, string tableName, string columnName, int rowNumber)
        {
            if (value is DateTime dateTime)
                return dateTime.Ticks;
            if (value is double serialDate)
                return DateTime.FromOADate(serialDate).Ticks;
            var text = ReadString(value, tableName, columnName, rowNumber);
            if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out dateTime))
                return dateTime.Ticks;
            throw InvalidValue(tableName, columnName, rowNumber, value, "an ISO-8601 date/time or Excel date");
        }

        public static long ReadDateTimeOffsetTicks(object? value, string tableName, string columnName, int rowNumber)
        {
            if (value is DateTime dateTime)
                return new DateTimeOffset(dateTime).UtcTicks;
            var text = ReadString(value, tableName, columnName, rowNumber);
            if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var result))
                return result.UtcTicks;
            throw InvalidValue(tableName, columnName, rowNumber, value, "an ISO-8601 date/time with offset");
        }

        public static long ReadTimeSpanTicks(object? value, string tableName, string columnName, int rowNumber)
        {
            if (value is TimeSpan timeSpan)
                return timeSpan.Ticks;
            if (value is double dayFraction)
                return TimeSpan.FromDays(dayFraction).Ticks;
            var text = ReadString(value, tableName, columnName, rowNumber);
            if (TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out timeSpan))
                return timeSpan.Ticks;
            throw InvalidValue(tableName, columnName, rowNumber, value, "a time span");
        }

        public static string ReadGuid(object? value, string tableName, string columnName, int rowNumber)
        {
            var text = ReadString(value, tableName, columnName, rowNumber);
            if (Guid.TryParse(text, out var result))
                return result.ToString();
            throw InvalidValue(tableName, columnName, rowNumber, value, "a GUID");
        }

        public static bool TryReadEnumName(object? value, out string name)
        {
            if (value is string text && !long.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
            {
                name = text.Trim();
                return true;
            }

            name = string.Empty;
            return false;
        }

        public static InvalidDataException InvalidEnum(
            string tableName,
            string columnName,
            int rowNumber,
            object? value,
            string enumName) =>
            InvalidValue(tableName, columnName, rowNumber, value, $"a named or numeric {enumName} value");

        private static bool IsNull(object? value) =>
            value == null || value == DBNull.Value || value is string text && string.IsNullOrWhiteSpace(text);

        private static InvalidDataException RequiredValue(string tableName, string columnName, int rowNumber) =>
            new($"Worksheet '{tableName}', row {rowNumber}, column '{columnName}' requires a value.");

        private static InvalidDataException InvalidValue(
            string tableName,
            string columnName,
            int rowNumber,
            object? value,
            string expected) =>
            new($"Worksheet '{tableName}', row {rowNumber}, column '{columnName}' has value '{value}', expected {expected}.");
    }
}

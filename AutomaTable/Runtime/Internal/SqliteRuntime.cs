using System;
using System.Collections.Generic;
using System.IO;
using SQLitePCL;

namespace AutomaTable.Runtime.Internal
{
    internal static class SqliteRuntime
    {
        public static sqlite3_stmt Prepare(sqlite3 database, string sql)
        {
            var result = raw.sqlite3_prepare_v2(database, sql, out var statement);
            if (result != raw.SQLITE_OK)
                throw CreateException(database, "prepare query", result);

            return statement;
        }

        public static void BindInt64(sqlite3 database, sqlite3_stmt statement, int index, long value)
        {
            EnsureSuccess(database, "bind integer", raw.sqlite3_bind_int64(statement, index, value));
        }

        public static void BindNullableInt64(sqlite3 database, sqlite3_stmt statement, int index, long? value)
        {
            var result = value.HasValue
                ? raw.sqlite3_bind_int64(statement, index, value.Value)
                : raw.sqlite3_bind_null(statement, index);
            EnsureSuccess(database, "bind integer", result);
        }

        public static void BindDouble(sqlite3 database, sqlite3_stmt statement, int index, double value)
        {
            EnsureSuccess(database, "bind real", raw.sqlite3_bind_double(statement, index, value));
        }

        public static void BindText(sqlite3 database, sqlite3_stmt statement, int index, string? value)
        {
            var result = value == null
                ? raw.sqlite3_bind_null(statement, index)
                : raw.sqlite3_bind_text(statement, index, value);
            EnsureSuccess(database, "bind text", result);
        }

        public static void BindBlob(sqlite3 database, sqlite3_stmt statement, int index, ReadOnlyMemory<byte> value)
        {
            var result = raw.sqlite3_bind_blob(statement, index, value.Span);
            EnsureSuccess(database, "bind blob", result);
        }

        public static void BindBlob(sqlite3 database, sqlite3_stmt statement, int index, ReadOnlyMemory<byte>? value)
        {
            var result = value.HasValue
                ? raw.sqlite3_bind_blob(statement, index, value.Value.Span)
                : raw.sqlite3_bind_null(statement, index);
            EnsureSuccess(database, "bind blob", result);
        }

        public static long ReadInt64(sqlite3_stmt statement, int index)
        {
            ThrowIfNull(statement, index);
            return raw.sqlite3_column_int64(statement, index);
        }

        public static long? ReadNullableInt64(sqlite3_stmt statement, int index)
        {
            return raw.sqlite3_column_type(statement, index) == raw.SQLITE_NULL
                ? null
                : raw.sqlite3_column_int64(statement, index);
        }

        public static double ReadDouble(sqlite3_stmt statement, int index)
        {
            ThrowIfNull(statement, index);
            return raw.sqlite3_column_double(statement, index);
        }

        public static string ReadText(sqlite3_stmt statement, int index)
        {
            ThrowIfNull(statement, index);
            return raw.sqlite3_column_text(statement, index).utf8_to_string();
        }

        public static string? ReadNullableText(sqlite3_stmt statement, int index)
        {
            return raw.sqlite3_column_type(statement, index) == raw.SQLITE_NULL
                ? null
                : raw.sqlite3_column_text(statement, index).utf8_to_string();
        }

        public static ReadOnlyMemory<byte> ReadBlob(sqlite3_stmt statement, int index)
        {
            ThrowIfNull(statement, index);
            return new ReadOnlyMemory<byte>(raw.sqlite3_column_blob(statement, index).ToArray());
        }

        public static ReadOnlyMemory<byte>? ReadNullableBlob(sqlite3_stmt statement, int index)
        {
            return raw.sqlite3_column_type(statement, index) == raw.SQLITE_NULL
                ? null
                : new ReadOnlyMemory<byte>(raw.sqlite3_column_blob(statement, index).ToArray());
        }

        public static Exception CreateException(sqlite3 database, string operation, int result)
        {
            var message = raw.sqlite3_errmsg(database).utf8_to_string();
            return new InvalidOperationException(
                $"SQLite failed to {operation}. Result: {result}. Message: {message}");
        }

        public static void ValidateTable(
            sqlite3 database,
            string tableName,
            IReadOnlyList<string> expectedColumns,
            string integerPrimaryKeyColumn)
        {
            var actualColumns = new Dictionary<string, TableColumnSchema>(StringComparer.OrdinalIgnoreCase);
            var primaryKeyColumnCount = 0;
            var statement = Prepare(database, "PRAGMA table_info(\"" + QuoteIdentifier(tableName) + "\")");
            try
            {
                while (true)
                {
                    var result = raw.sqlite3_step(statement);
                    if (result == raw.SQLITE_ROW)
                    {
                        var columnName = ReadText(statement, 1);
                        var declaredType = ReadText(statement, 2);
                        var primaryKeyOrdinal = checked((int)raw.sqlite3_column_int64(statement, 5));
                        actualColumns.Add(
                            columnName,
                            new TableColumnSchema(declaredType, primaryKeyOrdinal));
                        if (primaryKeyOrdinal > 0)
                            primaryKeyColumnCount++;
                        continue;
                    }

                    if (result == raw.SQLITE_DONE)
                        break;

                    throw CreateException(database, "read table schema", result);
                }
            }
            finally
            {
                raw.sqlite3_finalize(statement);
            }

            if (actualColumns.Count == 0)
                throw new InvalidDataException($"Required SQLite table '{tableName}' does not exist.");

            for (var i = 0; i < expectedColumns.Count; i++)
            {
                if (!actualColumns.ContainsKey(expectedColumns[i]))
                    throw new InvalidDataException(
                        $"Required SQLite column '{tableName}.{expectedColumns[i]}' does not exist.");
            }

            if (!actualColumns.TryGetValue(integerPrimaryKeyColumn, out var primaryKey) ||
                !string.Equals(primaryKey.DeclaredType.Trim(), "INTEGER", StringComparison.OrdinalIgnoreCase) ||
                primaryKey.PrimaryKeyOrdinal != 1 ||
                primaryKeyColumnCount != 1)
            {
                throw new InvalidDataException(
                    $"Required SQLite column '{tableName}.{integerPrimaryKeyColumn}' must be the table's single INTEGER PRIMARY KEY.");
            }
        }

        public static void ValidateIndex(
            sqlite3 database,
            string tableName,
            string indexName,
            IReadOnlyList<string> expectedColumns,
            bool requireUnique)
        {
            var indexBelongsToTable = false;
            var isUnique = false;
            var indexListStatement = Prepare(database, "PRAGMA index_list(\"" + QuoteIdentifier(tableName) + "\")");
            try
            {
                while (true)
                {
                    var result = raw.sqlite3_step(indexListStatement);
                    if (result == raw.SQLITE_ROW)
                    {
                        if (string.Equals(ReadText(indexListStatement, 1), indexName, StringComparison.OrdinalIgnoreCase))
                        {
                            indexBelongsToTable = true;
                            isUnique = raw.sqlite3_column_int64(indexListStatement, 2) != 0;
                            break;
                        }

                        continue;
                    }

                    if (result == raw.SQLITE_DONE)
                        break;

                    throw CreateException(database, "read table indexes", result);
                }
            }
            finally
            {
                raw.sqlite3_finalize(indexListStatement);
            }

            if (!indexBelongsToTable)
                throw new InvalidDataException(
                    $"Required SQLite index '{indexName}' does not exist on table '{tableName}'.");

            if (requireUnique && !isUnique)
                throw new InvalidDataException(
                    $"Required SQLite index '{indexName}' on table '{tableName}' must be UNIQUE.");

            var actualColumns = new List<string>();
            var statement = Prepare(database, "PRAGMA index_info(\"" + QuoteIdentifier(indexName) + "\")");
            try
            {
                while (true)
                {
                    var result = raw.sqlite3_step(statement);
                    if (result == raw.SQLITE_ROW)
                    {
                        actualColumns.Add(ReadText(statement, 2));
                        continue;
                    }

                    if (result == raw.SQLITE_DONE)
                        break;

                    throw CreateException(database, "read index schema", result);
                }
            }
            finally
            {
                raw.sqlite3_finalize(statement);
            }

            if (actualColumns.Count == 0)
                throw new InvalidDataException($"Required SQLite index '{indexName}' does not exist.");

            if (actualColumns.Count != expectedColumns.Count)
                throw new InvalidDataException($"SQLite index '{indexName}' has an unexpected column count.");

            for (var i = 0; i < expectedColumns.Count; i++)
            {
                if (!string.Equals(actualColumns[i], expectedColumns[i], StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException(
                        $"SQLite index '{indexName}' has an unexpected column at position {i}.");
            }
        }

        private static void EnsureSuccess(sqlite3 database, string operation, int result)
        {
            if (result != raw.SQLITE_OK)
                throw CreateException(database, operation, result);
        }

        private static void ThrowIfNull(sqlite3_stmt statement, int index)
        {
            if (raw.sqlite3_column_type(statement, index) == raw.SQLITE_NULL)
                throw new InvalidDataException($"SQLite returned NULL for required column position {index}.");
        }

        private static string QuoteIdentifier(string identifier) => identifier.Replace("\"", "\"\"");

        private readonly struct TableColumnSchema
        {
            public TableColumnSchema(string declaredType, int primaryKeyOrdinal)
            {
                DeclaredType = declaredType;
                PrimaryKeyOrdinal = primaryKeyOrdinal;
            }

            public string DeclaredType { get; }
            public int PrimaryKeyOrdinal { get; }
        }
    }
}

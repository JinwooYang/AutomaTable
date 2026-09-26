using System.IO;
using SQLitePCL;

namespace AutomaTable.Importer.Runtime
{
    internal static class SqliteWriteRuntime
    {
        public static sqlite3_stmt Prepare(sqlite3 database, string sql)
        {
            var result = raw.sqlite3_prepare_v2(database, sql, out var statement);
            if (result != raw.SQLITE_OK)
                throw CreateException(database, "prepare insert", result);
            return statement;
        }

        public static void Reset(sqlite3 database, sqlite3_stmt statement)
        {
            EnsureSuccess(database, "reset insert", raw.sqlite3_reset(statement));
            EnsureSuccess(database, "clear insert bindings", raw.sqlite3_clear_bindings(statement));
        }

        public static void Step(sqlite3 database, sqlite3_stmt statement)
        {
            var result = raw.sqlite3_step(statement);
            if (result != raw.SQLITE_DONE)
                throw CreateException(database, "insert row", result);
        }

        public static void Finalize(sqlite3_stmt statement) => raw.sqlite3_finalize(statement);

        public static void BindInt64(sqlite3 database, sqlite3_stmt statement, int index, long value) =>
            EnsureSuccess(database, "bind integer", raw.sqlite3_bind_int64(statement, index, value));

        public static void BindNullableInt64(sqlite3 database, sqlite3_stmt statement, int index, long? value) =>
            EnsureSuccess(database, "bind nullable integer", value.HasValue
                ? raw.sqlite3_bind_int64(statement, index, value.Value)
                : raw.sqlite3_bind_null(statement, index));

        public static void BindDouble(sqlite3 database, sqlite3_stmt statement, int index, double value) =>
            EnsureSuccess(database, "bind real", raw.sqlite3_bind_double(statement, index, value));

        public static void BindText(sqlite3 database, sqlite3_stmt statement, int index, string? value) =>
            EnsureSuccess(database, "bind text", value == null
                ? raw.sqlite3_bind_null(statement, index)
                : raw.sqlite3_bind_text(statement, index, value));

        public static void BindBlob(sqlite3 database, sqlite3_stmt statement, int index, ReadOnlyMemory<byte> value) =>
            EnsureSuccess(database, "bind blob", raw.sqlite3_bind_blob(statement, index, value.Span));

        public static void BindNullableBlob(sqlite3 database, sqlite3_stmt statement, int index, ReadOnlyMemory<byte>? value) =>
            EnsureSuccess(database, "bind nullable blob", value.HasValue
                ? raw.sqlite3_bind_blob(statement, index, value.Value.Span)
                : raw.sqlite3_bind_null(statement, index));

        public static Exception CreateException(sqlite3 database, string operation, int result)
        {
            var message = raw.sqlite3_errmsg(database).utf8_to_string();
            return new InvalidDataException(
                $"SQLite failed to {operation}. Result: {result}. Message: {message}");
        }

        private static void EnsureSuccess(sqlite3 database, string operation, int result)
        {
            if (result != raw.SQLITE_OK)
                throw CreateException(database, operation, result);
        }
    }
}

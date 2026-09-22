using System;
using System.IO;
using NUnit.Framework;

namespace DataTable.Tests.Infrastructure
{
    internal static class TableDataTestEnvironment
    {
        public static string DatabasePath => ResolvePath(
            "DATATABLE_TEST_DB_PATH",
            Path.Combine(TestContext.CurrentContext.TestDirectory, "TestData", "table.db"));

        public static string ResourcesPath => ResolvePath(
            "DATATABLE_TEST_RESOURCES_PATH",
            Path.Combine(TestContext.CurrentContext.TestDirectory, "Resources"));

        private static string ResolvePath(string environmentVariable, string defaultPath)
        {
            var configured = Environment.GetEnvironmentVariable(environmentVariable);
            return Path.GetFullPath(string.IsNullOrWhiteSpace(configured) ? defaultPath : configured);
        }
    }
}

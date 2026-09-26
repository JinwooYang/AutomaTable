using System;
using System.IO;
using NUnit.Framework;

namespace AutomaTable.Tests.Infrastructure
{
    internal static class TableDataTestEnvironment
    {
        public static string DatabasePath => ResolvePath(
            "AUTOMATABLE_TEST_DB_PATH",
            Path.Combine(TestContext.CurrentContext.TestDirectory, "TestData", "table.db"));

        public static string ResourcesPath => ResolvePath(
            "AUTOMATABLE_TEST_RESOURCES_PATH",
            Path.Combine(TestContext.CurrentContext.TestDirectory, "Resources"));

        private static string ResolvePath(string environmentVariable, string defaultPath)
        {
            var configured = Environment.GetEnvironmentVariable(environmentVariable);
            return Path.GetFullPath(string.IsNullOrWhiteSpace(configured) ? defaultPath : configured);
        }
    }
}

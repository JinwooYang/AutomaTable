using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AutomaTable.Primitives;
using AutomaTable.Runtime;
using AutomaTable.Tests.Models.Items;
using AutomaTable.Tests.Models.Quests;
using AutomaTable.Tool;
using AutomaTable.Tool.ProjectSystem;
using AutomaTable.Tool.Validation;
using NUnit.Framework;

namespace AutomaTable.Tests.Tool;

[TestFixture]
public sealed class ToolPipelineTests
{
    [Test]
    public async Task BuildAndValidate_UsesConditionalSchemaWithoutImporterGenerator()
    {
        using var directory = TemporaryDirectory.Create();
        var inputDirectory = Path.Combine(directory.Path, "Input");
        Directory.CreateDirectory(inputDirectory);
        File.Copy(Path.Combine(GetInputDirectory(), "ItemData.xlsx"), Path.Combine(inputDirectory, "ItemsWorkbook.xlsx"));
        File.Copy(Path.Combine(GetInputDirectory(), "QuestData.xlsx"), Path.Combine(inputDirectory, "GameplayWorkbook.xlsx"));
        var outputPath = Path.Combine(directory.Path, "table.db");

        var repositoryRoot = FindRepositoryRoot();
        var resolved = ProjectSchemaResolver.Resolve(
            repositoryRoot,
            Path.Combine(repositoryRoot, "AutomaTable.Tests", "AutomaTable.Tests.csproj"));
        var schemaAssembly = Directory.EnumerateFiles(
                Path.Combine(repositoryRoot, "AutomaTable.Tests", "obj", "AutomaTable", "SchemaBuild", "bin"),
                "AutomaTable.Tests.dll",
                SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .First();
        var schemaAssemblyWriteTime = File.GetLastWriteTimeUtc(schemaAssembly);
        _ = ProjectSchemaResolver.Resolve(
            repositoryRoot,
            Path.Combine(repositoryRoot, "AutomaTable.Tests", "AutomaTable.Tests.csproj"));
        Assert.That(File.GetLastWriteTimeUtc(schemaAssembly), Is.EqualTo(schemaAssemblyWriteTime),
            "An unchanged schema build should reuse the incremental artifacts without rebuilding the DLL.");
        var result = TableDatabaseBuilder.Build(resolved.Manifest, inputDirectory, outputPath);

        Assert.That(resolved.Manifest.Tables.Select(static table => table.Name),
            Is.EquivalentTo(new[] { "ItemData", "QuestData" }));
        Assert.That(result.WorkbookCount, Is.EqualTo(2));
        Assert.That(result.TotalRowCount, Is.EqualTo(2));

        var validation = TableDataValidator.Validate(
            resolved.Manifest,
            outputPath,
            Path.Combine(TestContext.CurrentContext.TestDirectory, "Resources"));
        Assert.That(validation.Succeeded, Is.True,
            string.Join(Environment.NewLine, validation.Checks.Where(static check => check.Failure != null)
                .Select(static check => check.Subject + ": " + check.Failure)));

        using var database = new TableDatabase();
        await database.InitializeAsync(outputPath);
        var quest = database.Quest.FindById(new Id<QuestData>(1));
        Assert.That(quest, Is.Not.Null);
        Assert.That(quest!.Type, Is.EqualTo(QuestType.Sub));
        Assert.That(quest.RepeatType, Is.EqualTo(QuestRepeatType.Daily));
        Assert.That(quest.IconAddress.Value, Is.EqualTo("Quest/Icons/Main.txt"));

        var item = database.Item.FindById(quest.RewardItemId);
        Assert.That(item, Is.Not.Null);
        Assert.That(item!.Id, Is.EqualTo(new Id<ItemData>(100)));
    }

    [Test]
    public void Build_DuplicateWorksheetNameAcrossFiles_FailsBeforeCreatingDatabase()
    {
        using var directory = TemporaryDirectory.Create();
        File.Copy(Path.Combine(GetInputDirectory(), "ItemData.xlsx"), Path.Combine(directory.Path, "ItemData.xlsx"));
        File.Copy(Path.Combine(GetInputDirectory(), "ItemData.xlsx"), Path.Combine(directory.Path, "Duplicate.xlsx"));
        File.Copy(Path.Combine(GetInputDirectory(), "QuestData.xlsx"), Path.Combine(directory.Path, "QuestData.xlsx"));
        var outputPath = Path.Combine(directory.Path, "table.db");
        var repositoryRoot = FindRepositoryRoot();
        var resolved = ProjectSchemaResolver.Resolve(
            repositoryRoot,
            Path.Combine(repositoryRoot, "AutomaTable.Tests", "AutomaTable.Tests.csproj"));

        var exception = Assert.Throws<InvalidDataException>(() =>
            TableDatabaseBuilder.Build(resolved.Manifest, directory.Path, outputPath));

        Assert.That(exception!.Message, Does.Contain("Worksheet 'ItemData' is duplicated"));
        Assert.That(File.Exists(outputPath), Is.False);
    }

    private static string GetInputDirectory() =>
        Path.Combine(TestContext.CurrentContext.TestDirectory, "ImporterInput");

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, "AutomaTable.sln")))
                return current.FullName;
            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the AutomaTable repository root.");
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private TemporaryDirectory(string path)
        {
            Path = path;
        }

        public string Path { get; }

        public static TemporaryDirectory Create()
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "AutomaTable.Tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return new TemporaryDirectory(path);
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, true);
        }
    }
}

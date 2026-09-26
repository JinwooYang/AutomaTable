using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using AutomaTable.Importer;
using AutomaTable.Models.Items;
using AutomaTable.Models.Quests;
using AutomaTable.Primitives;
using AutomaTable.Runtime;
using NUnit.Framework;

namespace AutomaTable.Tests.Importer
{
    [TestFixture]
    public sealed class TableDatabaseImporterTests
    {
        [Test]
        public async Task Import_CreatesDatabaseThatRuntimeCanRead()
        {
            using var directory = TemporaryDirectory.Create();
            var inputDirectory = Path.Combine(directory.Path, "Input");
            Directory.CreateDirectory(inputDirectory);
            File.Copy(Path.Combine(GetInputDirectory(), "ItemData.xlsx"), Path.Combine(inputDirectory, "ItemsWorkbook.xlsx"));
            File.Copy(Path.Combine(GetInputDirectory(), "QuestData.xlsx"), Path.Combine(inputDirectory, "GameplayWorkbook.xlsx"));
            var outputPath = Path.Combine(directory.Path, "table.db");

            var result = TableDatabaseImporter.Import(inputDirectory, outputPath);

            Assert.That(result.WorkbookCount, Is.EqualTo(2));
            Assert.That(result.TotalRowCount, Is.EqualTo(2));
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

            using var preloadedDatabase = new TableDatabase();
            await preloadedDatabase.InitializeAsync(outputPath, TableDatabaseOptions.PreloadAll);
            var quests = preloadedDatabase.Quest.FindAllByTypeAndRepeatType(
                QuestType.Sub,
                QuestRepeatType.Daily);
            Assert.That(quests, Is.TypeOf<List<QuestData>>());
            Assert.That(quests, Has.Count.EqualTo(1));
            Assert.That(quests[0].Id, Is.EqualTo(new Id<QuestData>(1)));
        }

        [Test]
        public void Import_DuplicateWorksheetNameAcrossFiles_FailsBeforeCreatingDatabase()
        {
            using var directory = TemporaryDirectory.Create();
            var directoryPath = directory.Path;
            File.Copy(Path.Combine(GetInputDirectory(), "ItemData.xlsx"), Path.Combine(directoryPath, "ItemData.xlsx"));
            File.Copy(Path.Combine(GetInputDirectory(), "ItemData.xlsx"), Path.Combine(directoryPath, "Duplicate.xlsx"));
            File.Copy(Path.Combine(GetInputDirectory(), "QuestData.xlsx"), Path.Combine(directoryPath, "QuestData.xlsx"));
            var outputPath = Path.Combine(directoryPath, "table.db");

            var exception = Assert.Throws<InvalidDataException>(() =>
                TableDatabaseImporter.Import(directoryPath, outputPath));

            Assert.That(exception!.Message, Does.Contain("Worksheet 'ItemData' is duplicated"));
            Assert.That(File.Exists(outputPath), Is.False);
        }

        private static string GetInputDirectory() =>
            Path.Combine(TestContext.CurrentContext.TestDirectory, "ImporterInput");

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
}

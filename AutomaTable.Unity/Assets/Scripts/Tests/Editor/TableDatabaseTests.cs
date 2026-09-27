using AutomaTable.Primitives;
using AutomaTable.Runtime;
using AutomaTable.Unity.Models;
using NUnit.Framework;
using UnityEngine;

namespace AutomaTable.Unity.Tests
{
    public sealed class TableDatabaseTests
    {
        [Test]
        public void GeneratedRuntimeApiReadsBuiltDatabase()
        {
            AutomaTableUnityBootstrap.EnsureInitialized();

            var databasePath = AutomaTableDatabaseFile
                .PrepareAsync("AutomaTable/table.db")
                .GetAwaiter()
                .GetResult();

            using (var database = new TableDatabase())
            {
                database.InitializeAsync(databasePath).GetAwaiter().GetResult();

                var quest = database.Quest.FindById(new Id<QuestData>(1));
                Assert.That(quest, Is.Not.Null);
                Assert.That(quest!.Type, Is.EqualTo(QuestType.Sub));
                Assert.That(quest.RepeatType, Is.EqualTo(QuestRepeatType.Daily));
                Assert.That(quest.IconAddress.Value, Is.EqualTo("Quest/Icons/Main.txt"));

                var quests = database.Quest.FindAllByTypeAndRepeatType(
                    QuestType.Sub,
                    QuestRepeatType.Daily);
                Assert.That(quests, Has.Count.EqualTo(1));

                var item = database.Item.FindById(quest.RewardItemId);
                Assert.That(item, Is.Not.Null);
                Assert.That(item!.Id, Is.EqualTo(new Id<ItemData>(100)));
            }

            Assert.That(Resources.Load<TextAsset>("Quest/Icons/Main"), Is.Not.Null);
        }
    }
}

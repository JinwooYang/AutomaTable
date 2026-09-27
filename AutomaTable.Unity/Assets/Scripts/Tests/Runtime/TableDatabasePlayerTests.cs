using System.Collections;
using AutomaTable.Primitives;
using AutomaTable.Runtime;
using AutomaTable.Unity.Models;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace AutomaTable.Unity.Tests
{
    public sealed class TableDatabasePlayerTests
    {
        [UnityTest]
        public IEnumerator GeneratedRuntimeApiReadsBuiltDatabaseOnPlayer()
        {
            var prepareTask = AutomaTableDatabaseFile.PrepareAsync("AutomaTable/table.db");
            while (!prepareTask.IsCompleted)
            {
                yield return null;
            }

            if (prepareTask.IsFaulted)
            {
                throw prepareTask.Exception!.GetBaseException();
            }

            AutomaTableUnityBootstrap.EnsureInitialized();

            using (var database = new TableDatabase())
            {
                var initializeTask = database.InitializeAsync(prepareTask.Result);
                while (!initializeTask.IsCompleted)
                {
                    yield return null;
                }

                if (initializeTask.IsFaulted)
                {
                    throw initializeTask.Exception!.GetBaseException();
                }

                var quest = database.Quest.FindById(new Id<QuestData>(1));
                Assert.That(quest, Is.Not.Null);
                Assert.That(quest!.Type, Is.EqualTo(QuestType.Sub));
                Assert.That(quest.RepeatType, Is.EqualTo(QuestRepeatType.Daily));
                Assert.That(quest.IconAddress.Value, Is.EqualTo("Quest/Icons/Main.txt"));

                var item = database.Item.FindById(quest.RewardItemId);
                Assert.That(item, Is.Not.Null);
                Assert.That(item!.Id, Is.EqualTo(new Id<ItemData>(100)));
            }

            Assert.That(Resources.Load<TextAsset>("Quest/Icons/Main"), Is.Not.Null);
        }
    }
}

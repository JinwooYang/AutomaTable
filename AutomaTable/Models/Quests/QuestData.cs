using AutomaTable.Annotations;
using AutomaTable.Models.Items;
using AutomaTable.Primitives;

namespace AutomaTable.Models.Quests
{
    [TableRow]
    [FindAllBy(nameof(Type), nameof(RepeatType))]
    public sealed class QuestData
    {
        public Id<QuestData> Id { get; internal set; }
        public QuestType Type { get; internal set; }
        public QuestRepeatType RepeatType { get; internal set; }
        public AssetAddress IconAddress { get; internal set; }
        public Id<ItemData> RewardItemId { get; internal set; }
    }
}

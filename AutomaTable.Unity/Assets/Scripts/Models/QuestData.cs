using AutomaTable.Annotations;
using AutomaTable.Primitives;

namespace AutomaTable.Unity.Models
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

    public enum QuestType
    {
        Main,
        Sub
    }

    public enum QuestRepeatType
    {
        None,
        Daily,
        Weekly
    }
}

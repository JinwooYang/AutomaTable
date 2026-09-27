using AutomaTable.Annotations;
using AutomaTable.Primitives;

namespace AutomaTable.Unity.Models
{
    [TableRow]
    public sealed class ItemData
    {
        public Id<ItemData> Id { get; internal set; }
    }
}

using AutomaTable.Annotations;
using AutomaTable.Primitives;

namespace AutomaTable.Models.Items
{
    [TableRow]
    public sealed class ItemData
    {
        public Id<ItemData> Id { get; internal set; }
    }
}

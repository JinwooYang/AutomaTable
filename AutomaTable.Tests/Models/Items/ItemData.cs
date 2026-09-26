using AutomaTable.Annotations;
using AutomaTable.Primitives;

namespace AutomaTable.Tests.Models.Items
{
    [TableRow]
    public sealed class ItemData
    {
        public Id<ItemData> Id { get; internal set; }
    }
}

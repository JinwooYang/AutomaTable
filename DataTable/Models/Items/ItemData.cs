using DataTable.Annotations;
using DataTable.Primitives;

namespace DataTable.Models.Items
{
    [TableRow]
    public sealed class ItemData
    {
        public Id<ItemData> Id { get; internal set; }
    }
}

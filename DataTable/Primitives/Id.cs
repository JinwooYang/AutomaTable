using System;

namespace DataTable.Primitives
{
    public readonly struct Id<T> : IEquatable<Id<T>>
    {
        public Id(long rawId)
        {
            RawId = rawId;
        }

        public long RawId { get; }

        public bool Equals(Id<T> other) => RawId == other.RawId;
        public override bool Equals(object? obj) => obj is Id<T> other && Equals(other);
        public override int GetHashCode() => RawId.GetHashCode();
        public override string ToString() => RawId.ToString();

        public static bool operator ==(Id<T> left, Id<T> right) => left.Equals(right);
        public static bool operator !=(Id<T> left, Id<T> right) => !left.Equals(right);
    }
}

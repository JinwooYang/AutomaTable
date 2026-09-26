using System;

namespace AutomaTable.Primitives
{
    public readonly struct AssetAddress : IEquatable<AssetAddress>
    {
        public AssetAddress(string value)
        {
            Value = value ?? throw new ArgumentNullException(nameof(value));
        }

        public string Value { get; }

        public bool Equals(AssetAddress other) => string.Equals(Value, other.Value, StringComparison.Ordinal);
        public override bool Equals(object? obj) => obj is AssetAddress other && Equals(other);
        public override int GetHashCode() => Value == null ? 0 : StringComparer.Ordinal.GetHashCode(Value);
        public override string ToString() => Value ?? string.Empty;

        public static bool operator ==(AssetAddress left, AssetAddress right) => left.Equals(right);
        public static bool operator !=(AssetAddress left, AssetAddress right) => !left.Equals(right);
    }
}

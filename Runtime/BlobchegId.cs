using System;

namespace Blobcheg
{
    // Router tag byte + 24-bit row position, not a hash; tag 0 is reserved for "not assigned".
    [Serializable]
    public readonly struct BlobchegId : IEquatable<BlobchegId>
    {
        public const int IndexBits = 24;

        public const uint IndexMask = (1u << IndexBits) - 1;

        public const uint MaxIndex = IndexMask;

        public const uint NoneValue = 0; // what any zero-initialised field carries

        public readonly uint Value;

        public BlobchegId(uint value) => Value = value;

        public static BlobchegId None => default;

        public byte Tag => (byte)(Value >> IndexBits); // zero = no id handed out

        public uint Index => Value & IndexMask;

        public bool IsValid => (Value >> IndexBits) != 0;

        public static BlobchegId Make(byte tag, uint index)
        {
            if (tag == 0)
                throw new ArgumentOutOfRangeException(nameof(tag),
                    "Blobcheg: router tag zero is reserved for \"id not assigned\"");

            if (index > MaxIndex)
                throw new ArgumentOutOfRangeException(nameof(index),
                    $"Blobcheg: row {index} is past the router ceiling of {MaxIndex}");

            return new BlobchegId(((uint)tag << IndexBits) | index);
        }

        public static BlobchegId In(string routerName, uint index) // tools and tests only: consumers take ids from a carrier or a save
            => Make(BlobchegNaming.TagOf(routerName), index);

        public bool Equals(BlobchegId other) => Value == other.Value;

        public override bool Equals(object obj) => obj is BlobchegId other && Equals(other);

        public override int GetHashCode() => (int)Value;

        public override string ToString() => IsValid ? Tag + ":" + Index : "none";

        public static bool operator ==(BlobchegId a, BlobchegId b) => a.Value == b.Value;

        public static bool operator !=(BlobchegId a, BlobchegId b) => a.Value != b.Value;
    }

    // Lets BlobchegIdRef ask its type parameter for the router name and reject a foreign asset.
    public interface IBlobchegRouter
    {
        string Name { get; } // also the name of its file
    }
}

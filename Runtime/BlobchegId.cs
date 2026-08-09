using System;

namespace Blobcheg
{
    /// <summary>
    /// The name of a node, shared by every base of a router. One <c>uint</c> of two parts: the high
    /// byte is the router tag, the low three are the position of the node's row in its file.
    ///
    /// A position, not a hash: the address of a row is <c>array[index]</c>, with no tables, collisions
    /// or probing. Hence the properties — editing values does not move an id, only the appearance and
    /// the deletion of a node do.
    ///
    /// The tag is needed because a bare number remembers no kinship: an id handed out by ANOTHER router
    /// would fall into the range of this one and quietly return someone else's row. The tag also closes
    /// the second hole: tag zero is reserved, so <c>default(BlobchegId)</c> means "not assigned" and
    /// not the first node of a base.
    ///
    /// The price is a ceiling of 16,777,216 nodes per router. There is no such thing as that many
    /// nodes; that many assets do not open.
    /// </summary>
    [Serializable]
    public readonly struct BlobchegId : IEquatable<BlobchegId>
    {
        /// <summary>How many low bits the row position takes.</summary>
        public const int IndexBits = 24;

        public const uint IndexMask = (1u << IndexBits) - 1;

        /// <summary>There are no more rows in one router than this.</summary>
        public const uint MaxIndex = IndexMask;

        /// <summary>"Id not assigned". The same value any zero-initialised field carries.</summary>
        public const uint NoneValue = 0;

        public readonly uint Value;

        public BlobchegId(uint value) => Value = value;

        public static BlobchegId None => default;

        /// <summary>The tag of the router that handed out the id. Zero means no id was handed out.</summary>
        public byte Tag => (byte)(Value >> IndexBits);

        /// <summary>The position of the row in the router file.</summary>
        public uint Index => Value & IndexMask;

        public bool IsValid => (Value >> IndexBits) != 0;

        /// <summary>Assemble an id from a tag and a position. A zero tag and a position past the ceiling are errors.</summary>
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

        /// <summary>
        /// The id of a row by the router name — the path of tools and tests. A consumer does not
        /// assemble an id: it takes it from a carrier or from a save.
        /// </summary>
        public static BlobchegId In(string routerName, uint index)
            => Make(BlobchegNaming.TagOf(routerName), index);

        public bool Equals(BlobchegId other) => Value == other.Value;

        public override bool Equals(object obj) => obj is BlobchegId other && Equals(other);

        public override int GetHashCode() => (int)Value;

        public override string ToString() => IsValid ? Tag + ":" + Index : "none";

        public static bool operator ==(BlobchegId a, BlobchegId b) => a.Value == b.Value;

        public static bool operator !=(BlobchegId a, BlobchegId b) => a.Value != b.Value;
    }

    /// <summary>
    /// Implemented by the codegen on every router struct. It exists for exactly one reason: so that a
    /// <see cref="BlobchegIdRef{TRouter}"/> field can ask its type parameter for the router name and
    /// reject the asset of a foreign one.
    /// </summary>
    public interface IBlobchegRouter
    {
        /// <summary>The name of the router, also the name of its file.</summary>
        string Name { get; }
    }
}

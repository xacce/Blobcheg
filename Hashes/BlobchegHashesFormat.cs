using System;
using System.Runtime.InteropServices;

namespace Blobcheg
{
    /// <summary>
    /// The format of a hash table file. The same header as a base and a router, plus a prolog and six
    /// arrays: two for the table itself, one for "hash by row number" and three for the reverse lanes
    /// "offset → row", one lane per base of the router.
    ///
    /// The table is computed during a rebuild and baked ready: at runtime it is not built but read.
    /// Hence the open addressing — a pair lies right in the array at slot
    /// <c>hash &amp; (Capacity - 1)</c>, and a taken slot leads on to the next. There are no chains, no
    /// insertions and no allocations on load.
    ///
    /// The reader does NOT compute the layout: the array offsets lie in the prolog — as in the router
    /// and for the same reason.
    /// </summary>
    public static class BlobchegHashesFormat
    {
        /// <summary>The prolog follows immediately after the header.</summary>
        public const int PrologOffset = BlobchegFormat.HeaderSize;

        public const int PrologSize = 48;

        /// <summary>The table file name is derived from the router name, not set separately.</summary>
        public const string Suffix = "Hashes";

        public static string IdentityOf(string routerName)
        {
            if (string.IsNullOrEmpty(routerName))
                throw new ArgumentException("Blobcheg: empty router name", nameof(routerName));

            return routerName + Suffix;
        }

        /// <summary>
        /// The capacity of the table: a power of two no less than twice the number of rows. Half
        /// occupancy means one and a half probes on average with linear probing; there is nothing to
        /// save here, the file is twelve bytes per row as it is.
        /// </summary>
        public static uint CapacityFor(int count)
        {
            if (count < 0)
                throw new ArgumentOutOfRangeException(nameof(count), "Blobcheg: a negative number of rows");

            var capacity = 1u;
            while (capacity < (uint)count * 2)
            {
                capacity <<= 1;

                if (capacity == 0)
                    throw new ArgumentOutOfRangeException(nameof(count),
                        $"Blobcheg: {count} rows — the table capacity does not fit into a uint");
            }

            return capacity;
        }

        public static bool IsPowerOfTwo(uint value) => value != 0 && (value & (value - 1)) == 0;
    }

    /// <summary>The prolog of a table file. Exactly <see cref="BlobchegHashesFormat.PrologSize"/> bytes.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct BlobchegHashesProlog
    {
        /// <summary>Rows of the router — exactly as many as in its file, holes included.</summary>
        public uint Count;

        public uint DomainCount;

        /// <summary>The hash of the router bit numbering: the table and the router must be of one build.</summary>
        public ulong LayoutHash;

        /// <summary>A power of two, no less than <c>2 * Count</c>.</summary>
        public uint Capacity;

        /// <summary><c>ulong[Capacity]</c>, zero means an empty slot.</summary>
        public uint KeysOffset;

        /// <summary><c>uint[Capacity]</c>, the row number parallel to the key.</summary>
        public uint RowsOffset;

        /// <summary><c>ulong[Count]</c>, the hash by row number; zero is a hole from a deleted node.</summary>
        public uint RowHashOffset;

        /// <summary><c>uint[DomainCount + 1]</c>, the bounds of the reverse lanes.</summary>
        public uint BackIndexOffset;

        /// <summary><c>uint[Total]</c>, offsets in ascending order inside a lane.</summary>
        public uint BackOffsetsOffset;

        /// <summary><c>uint[Total]</c>, row numbers parallel to the offsets.</summary>
        public uint BackRowsOffset;

        /// <summary>The total length of the reverse lanes. Also the last element of <c>BackIndex</c>.</summary>
        public uint Total;

        /// <summary>
        /// The check performed on load, not a hot path. Array bounds are checked against the file
        /// length: otherwise a broken prolog would send the very first lookup into foreign memory.
        /// </summary>
        public void Validate(string what, int fileLength, int domainCount, ulong layoutHash)
        {
            if (LayoutHash != layoutHash)
                throw new InvalidOperationException(
                    $"Blobcheg: table '{what}' was built for a different set of bases (the file says {LayoutHash:X16}, " +
                    $"the code says {layoutHash:X16}) — rebuild the bases or build the code");

            if (DomainCount != (uint)domainCount)
                throw new InvalidOperationException(
                    $"Blobcheg: table '{what}' — the file holds {DomainCount} bases, the code holds {domainCount}");

            if (!BlobchegHashesFormat.IsPowerOfTwo(Capacity) || Capacity < (ulong)Count * 2)
                throw new InvalidOperationException(
                    $"Blobcheg: table '{what}' — a capacity of {Capacity} for {Count} rows will not do: " +
                    "a power of two no less than twice the number of rows is needed");

            var keysEnd = (long)KeysOffset + (long)Capacity * 8;
            var rowsEnd = (long)RowsOffset + (long)Capacity * 4;
            var rowHashEnd = (long)RowHashOffset + (long)Count * 8;
            var backIndexEnd = (long)BackIndexOffset + ((long)DomainCount + 1) * 4;
            var backOffsetsEnd = (long)BackOffsetsOffset + (long)Total * 4;
            var backRowsEnd = (long)BackRowsOffset + (long)Total * 4;

            if (KeysOffset < BlobchegHashesFormat.PrologOffset + BlobchegHashesFormat.PrologSize
                || keysEnd > fileLength
                || RowsOffset < keysEnd || rowsEnd > fileLength
                || RowHashOffset < rowsEnd || rowHashEnd > fileLength
                || BackIndexOffset < rowHashEnd || backIndexEnd > fileLength
                || BackOffsetsOffset < backIndexEnd || backOffsetsEnd > fileLength
                || BackRowsOffset < backOffsetsEnd || backRowsEnd > fileLength)
                throw new InvalidOperationException(
                    $"Blobcheg: table '{what}' — the prolog points past a file of {fileLength} B");
        }
    }
}

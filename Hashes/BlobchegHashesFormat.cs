using System;
using System.Runtime.InteropServices;

namespace Blobcheg
{
    // Baked at rebuild and only read at runtime, hence open addressing; array offsets live in the prolog.
    public static class BlobchegHashesFormat
    {
        public const int PrologOffset = BlobchegFormat.HeaderSize;

        public const int PrologSize = 48;

        public const string Suffix = "Hashes";

        public static string IdentityOf(string routerName)
        {
            if (string.IsNullOrEmpty(routerName))
                throw new ArgumentException("Blobcheg: empty router name", nameof(routerName));

            return routerName + Suffix;
        }

        public static uint CapacityFor(int count) // half occupancy keeps linear probing at ~1.5 probes
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

    [StructLayout(LayoutKind.Sequential)]
    public struct BlobchegHashesProlog // exactly BlobchegHashesFormat.PrologSize bytes
    {
        public uint Count; // router rows, holes included

        public uint DomainCount;

        public ulong LayoutHash; // router bit numbering: table and router must come from one build

        public uint Capacity;

        public uint KeysOffset; // ulong[Capacity], zero = empty slot

        public uint RowsOffset; // uint[Capacity], row parallel to the key

        public uint RowHashOffset; // ulong[Count], zero = hole from a deleted node

        public uint BackIndexOffset; // uint[DomainCount + 1], reverse lane bounds

        public uint BackOffsetsOffset; // uint[Total], ascending inside a lane

        public uint BackRowsOffset; // uint[Total], rows parallel to the offsets

        public uint Total; // also the last element of BackIndex

        // Bounds checked against file length so a broken prolog cannot send a lookup into foreign memory.
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

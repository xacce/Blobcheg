using System;
using System.Runtime.InteropServices;

namespace Blobcheg
{
    /// <summary>
    /// An entry of the debug section. In the editor it is always there — the type check on read and
    /// the tools' <c>Describe</c> stand on it. It does not travel into a release player: type and node
    /// names are not needed there, and what lies inside the binary is once again a question of trust.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct BlobchegDebugEntry
    {
        public uint Offset;
        public uint Size;

        /// <summary>BurstRuntime.GetHashCode32 of the record type, 0 means raw bytes.</summary>
        public uint TypeHash;

        /// <summary>The absolute offset of the "type, node" string pair in the file.</summary>
        public uint NameOffset;
    }

    public static class BlobchegDebugSection
    {
        /// <summary>'BDBG' in the byte order of the file.</summary>
        public const uint Magic = 0x47424442;

        /// <summary>magic + count ahead of the entry array.</summary>
        public const int PrologSize = 8;

        public const int EntrySize = 16;

        /// <summary>
        /// Finds an entry by its offset with a binary search. Entries in the section are sorted by
        /// Offset, just like the records themselves in the file.
        /// </summary>
        public static unsafe BlobchegDebugEntry* Find(byte* file, uint debugOffset, uint recordOffset)
        {
            var count = *(uint*)(file + debugOffset + 4);
            var entries = (BlobchegDebugEntry*)(file + debugOffset + PrologSize);

            var lo = 0;
            var hi = (int)count - 1;
            while (lo <= hi)
            {
                var mid = (lo + hi) >> 1;
                var at = entries[mid].Offset;
                if (at == recordOffset)
                    return entries + mid;

                if (at < recordOffset)
                    lo = mid + 1;
                else
                    hi = mid - 1;
            }

            return null;
        }

        /// <summary>Type and node names for an entry — the managed path, for editor tools only.</summary>
        public static unsafe void ReadNames(byte* file, in BlobchegDebugEntry entry,
            out string typeName, out string nodeName)
        {
            var p = file + entry.NameOffset;
            typeName = ReadString(ref p);
            nodeName = ReadString(ref p);
        }

        static unsafe string ReadString(ref byte* p)
        {
            var length = *(ushort*)p;
            p += 2;
            var value = System.Text.Encoding.UTF8.GetString(p, length);
            p += length;
            return value;
        }

        public static void ValidateProlog(uint magic)
        {
            if (magic != Magic)
                throw new InvalidOperationException(
                    $"Blobcheg: the debug section is not where the header promised (magic {magic:X8})");
        }
    }
}

using System;
using System.Runtime.InteropServices;

namespace Blobcheg
{
    /// <summary>
    /// Запись debug-секции. Секции нет ни в билде, ни в файле, собранном без дефайна
    /// <c>BLOBCHEG_DEBUG</c>: что лежит внутри бинарника — вопрос доверия, а это отладочный контур.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct BlobchegDebugEntry
    {
        public uint Offset;
        public uint Size;

        /// <summary>BurstRuntime.GetHashCode32 типа записи, 0 — сырые байты.</summary>
        public uint TypeHash;

        /// <summary>Абсолютный оффсет пары строк «тип, нода» в файле.</summary>
        public uint NameOffset;
    }

    public static class BlobchegDebugSection
    {
        /// <summary>'BDBG' в порядке байтов файла.</summary>
        public const uint Magic = 0x47424442;

        /// <summary>magic + count перед массивом записей.</summary>
        public const int PrologSize = 8;

        public const int EntrySize = 16;

        /// <summary>
        /// Ищет запись по её оффсету двоичным поиском. Записи в секции отсортированы по Offset,
        /// как и сами записи в файле.
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

        /// <summary>Имена типа и ноды по записи — managed-путь, только для инструментов едитора.</summary>
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
                    $"Blobcheg: debug-секция не там, где обещал header (magic {magic:X8})");
        }
    }
}

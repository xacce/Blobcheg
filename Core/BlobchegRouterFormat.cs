using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace Blobcheg
{
    /// <summary>
    /// Формат файла роутера. Тот же header, что у базы, плюс пролог и три массива: маска строки,
    /// начало её оффсетов и сами оффсеты. Строка — нода, бит — база, оффсеты упакованы подряд, без
    /// дырок под базы, в которых ноды нет.
    ///
    /// Раскладку читатель НЕ вычисляет: оффсеты массивов лежат в прологе. Иначе изменение раскладки
    /// молча уводило бы старого читателя на 16 байт вместо честного падения по версии.
    /// </summary>
    public static class BlobchegRouterFormat
    {
        /// <summary>Пролог идёт сразу за header'ом.</summary>
        public const int PrologOffset = BlobchegFormat.HeaderSize;

        public const int PrologSize = 32;

        /// <summary>'BRDG' в порядке байтов файла — пролог debug-секции роутера.</summary>
        public const uint DebugMagic = 0x47445242;

        /// <summary>Больше 64 баз в одном роутере — это не «мало бит», а неправильно нарезанный проект.</summary>
        public const int MaxDomains = 64;

        public static int MaskWidthFor(int domainCount)
        {
            if (domainCount <= 0)
                throw new ArgumentOutOfRangeException(nameof(domainCount),
                    "Blobcheg: роутер без единой базы — маршрутизировать нечего");

            if (domainCount <= 8)
                return 1;
            if (domainCount <= 16)
                return 2;
            if (domainCount <= 32)
                return 4;
            if (domainCount <= MaxDomains)
                return 8;

            throw new ArgumentOutOfRangeException(nameof(domainCount),
                $"Blobcheg: {domainCount} баз в одном роутере, потолок {MaxDomains}");
        }

        /// <summary>
        /// Единственное, что связывает нумерацию бит у кодогена и у едиторной сборки: они приходят к
        /// ней независимо, поэтому сходимость доказывается хешем, а не честным словом. Алгоритм
        /// продублирован в генераторе — менять только парно.
        /// </summary>
        public static ulong LayoutHash(IEnumerable<KeyValuePair<string, string>> domainsAndMembers, int maskWidth)
        {
            const ulong offsetBasis = 14695981039346656037;
            const ulong prime = 1099511628211;

            var hash = offsetBasis;

            foreach (var pair in domainsAndMembers)
            {
                Feed(ref hash, pair.Key);
                Feed(ref hash, "\n");
                Feed(ref hash, pair.Value);
                Feed(ref hash, "\n");
            }

            hash ^= (byte)maskWidth;
            hash *= prime;
            return hash;

            void Feed(ref ulong state, string value)
            {
                foreach (var b in Encoding.UTF8.GetBytes(value ?? string.Empty))
                {
                    state ^= b;
                    state *= prime;
                }
            }
        }
    }

    /// <summary>Пролог файла роутера. Ровно <see cref="BlobchegRouterFormat.PrologSize"/> байт.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct BlobchegRouterProlog
    {
        /// <summary>Строк, то есть нод роутера. Он же потолок <c>BlobchegId</c>.</summary>
        public uint Count;

        public uint DomainCount;

        /// <summary>Хеш нумерации бит: файл и кодоген обязаны сойтись.</summary>
        public ulong LayoutHash;

        public uint MasksOffset;
        public uint RowStartOffset;
        public uint OffsetsOffset;

        /// <summary>1, 2, 4 или 8 байт на маску — по числу баз.</summary>
        public uint MaskWidth;

        /// <summary>
        /// Проверка при подъёме, не hot path. Границы массивов сверяются с длиной файла: битый пролог
        /// иначе увёл бы чтение в чужую память по первому же <c>Get</c>.
        /// </summary>
        public void Validate(string what, int fileLength, int domainCount, ulong layoutHash)
        {
            if (LayoutHash != layoutHash)
                throw new InvalidOperationException(
                    $"Blobcheg: роутер '{what}' собран под другой набор баз (в файле {LayoutHash:X16}, " +
                    $"в коде {layoutHash:X16}) — пересобери базы или собери код");

            if (DomainCount != (uint)domainCount)
                throw new InvalidOperationException(
                    $"Blobcheg: роутер '{what}' — в файле {DomainCount} баз, в коде {domainCount}");

            if (MaskWidth != (uint)BlobchegRouterFormat.MaskWidthFor(domainCount))
                throw new InvalidOperationException(
                    $"Blobcheg: роутер '{what}' — ширина маски {MaskWidth} Б не отвечает {domainCount} базам");

            var masksEnd = (long)MasksOffset + (long)Count * MaskWidth;
            var rowStartEnd = (long)RowStartOffset + ((long)Count + 1) * 4;

            if (MasksOffset < BlobchegRouterFormat.PrologOffset + BlobchegRouterFormat.PrologSize
                || masksEnd > fileLength
                || RowStartOffset < masksEnd
                || rowStartEnd > fileLength
                || OffsetsOffset < rowStartEnd
                || OffsetsOffset > fileLength)
                throw new InvalidOperationException(
                    $"Blobcheg: роутер '{what}' — пролог указывает мимо файла длиной {fileLength} Б");
        }
    }
}

using System;
using System.Runtime.InteropServices;

namespace Blobcheg
{
    /// <summary>
    /// Формат файла таблицы хешей. Тот же header, что у базы и роутера, плюс пролог и шесть
    /// массивов: два на саму таблицу, один на «хеш по номеру строки» и три на обратные дорожки
    /// «оффсет → строка», по дорожке на каждую базу роутера.
    ///
    /// Таблица считается на пересборке и печётся готовой: в рантайме её не строят, а читают. Отсюда
    /// открытая адресация — пара лежит прямо в массиве по слоту <c>hash &amp; (Capacity - 1)</c>, а
    /// занятый слот уводит на следующий. Цепочек нет, вставок нет, аллокаций на подъёме нет.
    ///
    /// Раскладку читатель НЕ вычисляет: оффсеты массивов лежат в прологе — как у роутера и по той же
    /// причине.
    /// </summary>
    public static class BlobchegHashesFormat
    {
        /// <summary>Пролог идёт сразу за header'ом.</summary>
        public const int PrologOffset = BlobchegFormat.HeaderSize;

        public const int PrologSize = 48;

        /// <summary>Имя файла таблицы выводится из имени роутера, а не задаётся отдельно.</summary>
        public const string Suffix = "Hashes";

        public static string IdentityOf(string routerName)
        {
            if (string.IsNullOrEmpty(routerName))
                throw new ArgumentException("Blobcheg: пустое имя роутера", nameof(routerName));

            return routerName + Suffix;
        }

        /// <summary>
        /// Ёмкость таблицы: степень двойки не меньше удвоенного числа строк. Половинное заполнение —
        /// это в среднем полтора захода при линейном пробировании; экономить здесь нечего, файл и так
        /// двенадцать байт на строку.
        /// </summary>
        public static uint CapacityFor(int count)
        {
            if (count < 0)
                throw new ArgumentOutOfRangeException(nameof(count), "Blobcheg: отрицательное число строк");

            var capacity = 1u;
            while (capacity < (uint)count * 2)
            {
                capacity <<= 1;

                if (capacity == 0)
                    throw new ArgumentOutOfRangeException(nameof(count),
                        $"Blobcheg: {count} строк — ёмкость таблицы не лезет в uint");
            }

            return capacity;
        }

        public static bool IsPowerOfTwo(uint value) => value != 0 && (value & (value - 1)) == 0;
    }

    /// <summary>Пролог файла таблицы. Ровно <see cref="BlobchegHashesFormat.PrologSize"/> байт.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct BlobchegHashesProlog
    {
        /// <summary>Строк роутера — ровно столько же, сколько в его файле, включая дырки.</summary>
        public uint Count;

        public uint DomainCount;

        /// <summary>Хеш нумерации бит роутера: таблица и роутер обязаны быть одной сборки.</summary>
        public ulong LayoutHash;

        /// <summary>Степень двойки, не меньше <c>2 * Count</c>.</summary>
        public uint Capacity;

        /// <summary><c>ulong[Capacity]</c>, ноль — пустой слот.</summary>
        public uint KeysOffset;

        /// <summary><c>uint[Capacity]</c>, номер строки параллельно ключу.</summary>
        public uint RowsOffset;

        /// <summary><c>ulong[Count]</c>, хеш по номеру строки; ноль — дырка от удалённой ноды.</summary>
        public uint RowHashOffset;

        /// <summary><c>uint[DomainCount + 1]</c>, границы обратных дорожек.</summary>
        public uint BackIndexOffset;

        /// <summary><c>uint[Total]</c>, оффсеты по возрастанию внутри дорожки.</summary>
        public uint BackOffsetsOffset;

        /// <summary><c>uint[Total]</c>, номера строк параллельно оффсетам.</summary>
        public uint BackRowsOffset;

        /// <summary>Длина обратных дорожек суммарно. Она же последний элемент <c>BackIndex</c>.</summary>
        public uint Total;

        /// <summary>
        /// Проверка при подъёме, не hot path. Границы массивов сверяются с длиной файла: битый пролог
        /// иначе увёл бы первый же лукап в чужую память.
        /// </summary>
        public void Validate(string what, int fileLength, int domainCount, ulong layoutHash)
        {
            if (LayoutHash != layoutHash)
                throw new InvalidOperationException(
                    $"Blobcheg: таблица '{what}' собрана под другой набор баз (в файле {LayoutHash:X16}, " +
                    $"в коде {layoutHash:X16}) — пересобери базы или собери код");

            if (DomainCount != (uint)domainCount)
                throw new InvalidOperationException(
                    $"Blobcheg: таблица '{what}' — в файле {DomainCount} баз, в коде {domainCount}");

            if (!BlobchegHashesFormat.IsPowerOfTwo(Capacity) || Capacity < (ulong)Count * 2)
                throw new InvalidOperationException(
                    $"Blobcheg: таблица '{what}' — ёмкость {Capacity} при {Count} строках не годится: " +
                    "нужна степень двойки не меньше удвоенного числа строк");

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
                    $"Blobcheg: таблица '{what}' — пролог указывает мимо файла длиной {fileLength} Б");
        }
    }
}

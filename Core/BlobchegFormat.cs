using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Blobcheg
{
    /// <summary>
    /// Бинарный формат базы, version 1. Файл — это header и подряд лежащие выровненные байты;
    /// таблиц в релизном файле нет, смысл записям придаёт только оффсет, сохранённый потребителем.
    /// </summary>
    public static class BlobchegFormat
    {
        /// <summary>'BCHG' в порядке байтов файла.</summary>
        public const uint Magic = 0x47484342;

        /// <summary>
        /// 2 — появился роутер. Версия общая для всех файлов пакета: они производные и пересобираются
        /// вместе, а разные версии у базы и роутера были бы лишней осью рассинхрона.
        /// </summary>
        public const ushort Version = 2;

        /// <summary>Размер header'а и одновременно оффсет первой записи.</summary>
        public const int HeaderSize = 32;

        /// <summary>Старт каждой записи выровнен на это от начала файла.</summary>
        public const int RecordAlign = 16;

        /// <summary>Бит flags: в файле есть debug-секция.</summary>
        public const ushort FlagHasDebug = 1 << 0;

        /// <summary>Бит flags: файл — роутер, а не база. Перепутанные отбиваются на подъёме.</summary>
        public const ushort FlagRouter = 1 << 1;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int AlignUp(int value) => (value + (RecordAlign - 1)) & ~(RecordAlign - 1);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint AlignUp(uint value) => (value + (RecordAlign - 1)) & ~((uint)RecordAlign - 1);
    }

    /// <summary>Начало файла базы. Ровно <see cref="BlobchegFormat.HeaderSize"/> байт.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct BlobchegHeader
    {
        public uint Magic;
        public ushort Version;
        public ushort Flags;

        /// <summary>Полная длина файла — валидация транспорта.</summary>
        public uint FileLength;

        /// <summary>Абсолютный оффсет debug-секции, 0 — секции нет.</summary>
        public uint DebugOffset;

        /// <summary>xxHash3 всего, что после header'а. Integrity, сверяется всегда, без дефайнов.</summary>
        public ulong ContentHash;

        ulong _padding;

        public bool HasDebug => (Flags & BlobchegFormat.FlagHasDebug) != 0;

        public bool IsRouter => (Flags & BlobchegFormat.FlagRouter) != 0;

        /// <summary>
        /// Проверка при подъёме базы. Не hot path — вызывается раз на базу, поэтому не за дефайном.
        /// Любое расхождение бросает: база либо поднялась целиком, либо игра не поехала.
        /// </summary>
        public void Validate(string what, int actualLength, ulong actualContentHash, bool wantRouter = false)
        {
            if (Magic != BlobchegFormat.Magic)
                throw new InvalidOperationException(
                    $"Blobcheg: '{what}' — не файл blobcheg (magic {Magic:X8}, ожидался {BlobchegFormat.Magic:X8})");

            if (Version != BlobchegFormat.Version)
                throw new InvalidOperationException(
                    $"Blobcheg: '{what}' — версия формата {Version}, читатель понимает {BlobchegFormat.Version}");

            if (IsRouter != wantRouter)
                throw new InvalidOperationException(
                    $"Blobcheg: '{what}' — это файл {(IsRouter ? "роутера" : "базы")}, а поднимают его как " +
                    $"{(wantRouter ? "роутер" : "базу")}");

            if (FileLength != (uint)actualLength)
                throw new InvalidOperationException(
                    $"Blobcheg: '{what}' — обрезан или дописан: в header'е {FileLength} Б, прочитано {actualLength} Б");

            if (DebugOffset != 0 && (DebugOffset < BlobchegFormat.HeaderSize || DebugOffset >= FileLength))
                throw new InvalidOperationException(
                    $"Blobcheg: '{what}' — debug-секция по невозможному оффсету {DebugOffset} при длине {FileLength}");

            if (ContentHash != actualContentHash)
                throw new InvalidOperationException(
                    $"Blobcheg: '{what}' — не сошлась целостность: в header'е {ContentHash:X16}, посчитано {actualContentHash:X16}");
        }
    }
}

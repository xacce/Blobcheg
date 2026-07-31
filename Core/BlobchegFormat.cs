using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Blobcheg
{
    /// <summary>
    /// Что за файл лежит перед читателем. Вид пишется флагами в header и сверяется на подъёме:
    /// перепутанные файлы иначе поднимаются и молча отдают чужие байты.
    /// </summary>
    public enum BlobchegFileKind
    {
        Database = 0,
        Router = 1,

        /// <summary>Таблица хешей имён — файл сборки <c>Blobcheg.Hashes</c>.</summary>
        Hashes = 2,
    }

    /// <summary>
    /// Бинарный формат базы, version 1. Файл — это header и подряд лежащие выровненные байты;
    /// таблиц в релизном файле нет, смысл записям придаёт только оффсет, сохранённый потребителем.
    /// </summary>
    public static class BlobchegFormat
    {
        /// <summary>'BCHG' в порядке байтов файла.</summary>
        public const uint Magic = 0x47484342;

        /// <summary>
        /// 2 — появился роутер, 3 — у файла появилась личность (хеш имени в header'е), 4 — видов
        /// файла стало три и они больше не различаются одним булем. Версия общая для всех файлов
        /// пакета: они производные и пересобираются вместе, а разные версии у базы и роутера были бы
        /// лишней осью рассинхрона.
        /// </summary>
        public const ushort Version = 4;

        /// <summary>Размер header'а и одновременно оффсет первой записи.</summary>
        public const int HeaderSize = 32;

        /// <summary>Старт каждой записи выровнен на это от начала файла.</summary>
        public const int RecordAlign = 16;

        /// <summary>Бит flags: в файле есть debug-секция.</summary>
        public const ushort FlagHasDebug = 1 << 0;

        /// <summary>Бит flags: файл — роутер, а не база. Перепутанные отбиваются на подъёме.</summary>
        public const ushort FlagRouter = 1 << 1;

        /// <summary>Бит flags: файл — таблица хешей.</summary>
        public const ushort FlagHashes = 1 << 2;

        /// <summary>Флаги вида файла — то, что писатель кладёт в header, а читатель сверяет.</summary>
        public static ushort FlagsOf(BlobchegFileKind kind)
        {
            switch (kind)
            {
                case BlobchegFileKind.Database: return 0;
                case BlobchegFileKind.Router: return FlagRouter;
                case BlobchegFileKind.Hashes: return FlagHashes;
                default:
                    throw new ArgumentOutOfRangeException(nameof(kind), $"Blobcheg: неизвестный вид файла {kind}");
            }
        }

        /// <summary>
        /// Вид файла по флагам header'а. Два вида сразу — испорченный или собранный чужим
        /// инструментом файл, и он не «база по умолчанию», а ошибка.
        /// </summary>
        public static BlobchegFileKind KindOf(ushort flags)
        {
            var kindBits = flags & (FlagRouter | FlagHashes);

            switch (kindBits)
            {
                case 0: return BlobchegFileKind.Database;
                case FlagRouter: return BlobchegFileKind.Router;
                case FlagHashes: return BlobchegFileKind.Hashes;
                default:
                    throw new InvalidOperationException(
                        $"Blobcheg: в header'е сразу два вида файла (флаги {flags:X4})");
            }
        }

        /// <summary>«Это файл ...» — чей файл лежит перед читателем.</summary>
        public static string NameOf(BlobchegFileKind kind)
        {
            switch (kind)
            {
                case BlobchegFileKind.Router: return "роутера";
                case BlobchegFileKind.Hashes: return "таблицы хешей";
                default: return "базы";
            }
        }

        /// <summary>«Поднимают его как ...» — чем его считает читатель.</summary>
        public static string TargetOf(BlobchegFileKind kind)
        {
            switch (kind)
            {
                case BlobchegFileKind.Router: return "роутер";
                case BlobchegFileKind.Hashes: return "таблицу хешей";
                default: return "базу";
            }
        }

        /// <summary>«Это файл другого ...» — файлы одного вида, но переставленные местами.</summary>
        public static string OwnerOf(BlobchegFileKind kind)
        {
            switch (kind)
            {
                case BlobchegFileKind.Router: return "другого роутера";
                case BlobchegFileKind.Hashes: return "другой таблицы хешей";
                default: return "другого домена";
            }
        }

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

        /// <summary>
        /// Личность файла: <see cref="BlobchegNaming.NameHash"/> имени домена или роутера. Без неё
        /// два переставленных местами .bcheg поднимаются оба и молча отдают чужие байты — целостность
        /// у каждого своя и сходится.
        /// </summary>
        public ulong NameHash;

        public bool HasDebug => (Flags & BlobchegFormat.FlagHasDebug) != 0;

        public bool IsRouter => (Flags & BlobchegFormat.FlagRouter) != 0;

        /// <summary>Вид файла по флагам. Испорченные флаги бросают, а не отдают «базу».</summary>
        public BlobchegFileKind Kind => BlobchegFormat.KindOf(Flags);

        /// <summary>
        /// Проверка при подъёме базы. Не hot path — вызывается раз на базу, поэтому не за дефайном.
        /// Любое расхождение бросает: база либо поднялась целиком, либо игра не поехала.
        /// </summary>
        public void Validate(string what, int actualLength, ulong actualContentHash,
            BlobchegFileKind wantKind = BlobchegFileKind.Database)
        {
            if (Magic != BlobchegFormat.Magic)
                throw new InvalidOperationException(
                    $"Blobcheg: '{what}' — не файл blobcheg (magic {Magic:X8}, ожидался {BlobchegFormat.Magic:X8})");

            if (Version != BlobchegFormat.Version)
                throw new InvalidOperationException(
                    $"Blobcheg: '{what}' — версия формата {Version}, читатель понимает {BlobchegFormat.Version}");

            var kind = Kind;
            if (kind != wantKind)
                throw new InvalidOperationException(
                    $"Blobcheg: '{what}' — это файл {BlobchegFormat.NameOf(kind)}, а поднимают его как " +
                    $"{BlobchegFormat.TargetOf(wantKind)}");

            var wantedName = BlobchegNaming.NameHash(what);
            if (NameHash != wantedName)
                throw new InvalidOperationException(
                    $"Blobcheg: '{what}' — это файл {BlobchegFormat.OwnerOf(kind)} " +
                    $"(в header'е {NameHash:X16}, у '{what}' {wantedName:X16}). Файлы переставлены местами " +
                    "или пересобраны под другими именами");

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

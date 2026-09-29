using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Blobcheg
{
    // Kind flags are checked on load, so a mixed-up file cannot quietly hand out foreign bytes
    public enum BlobchegFileKind
    {
        Database = 0,
        Router = 1,

        Hashes = 2,
    }

    public static class BlobchegFormat // no tables: a record means only the offset the consumer kept
    {
        public const uint Magic = 0x47484342; // 'BCHG' in file byte order

        public const ushort Version = 4; // shared by every file kind: they are rebuilt together

        public const int HeaderSize = 32; // also the offset of the first record

        public const int RecordAlign = 16; // from the start of the file

        public const ushort FlagHasDebug = 1 << 0;

        public const ushort FlagRouter = 1 << 1;

        public const ushort FlagHashes = 1 << 2;

        public static ushort FlagsOf(BlobchegFileKind kind)
        {
            switch (kind)
            {
                case BlobchegFileKind.Database: return 0;
                case BlobchegFileKind.Router: return FlagRouter;
                case BlobchegFileKind.Hashes: return FlagHashes;
                default:
                    throw new ArgumentOutOfRangeException(nameof(kind), $"Blobcheg: unknown file kind {kind}");
            }
        }

        // Two kind bits at once is a corrupt or foreign file: an error, not "a base by default"
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
                        $"Blobcheg: the header claims two file kinds at once (flags {flags:X4})");
            }
        }

        public static string NameOf(BlobchegFileKind kind) // "this is a ... file"
        {
            switch (kind)
            {
                case BlobchegFileKind.Router: return "router";
                case BlobchegFileKind.Hashes: return "hash table";
                default: return "base";
            }
        }

        public static string TargetOf(BlobchegFileKind kind) // "it is being loaded as a ..."
        {
            switch (kind)
            {
                case BlobchegFileKind.Router: return "router";
                case BlobchegFileKind.Hashes: return "hash table";
                default: return "base";
            }
        }

        public static string OwnerOf(BlobchegFileKind kind) // "this is the file of another ..."
        {
            switch (kind)
            {
                case BlobchegFileKind.Router: return "another router";
                case BlobchegFileKind.Hashes: return "another hash table";
                default: return "another domain";
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int AlignUp(int value) => (value + (RecordAlign - 1)) & ~(RecordAlign - 1);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint AlignUp(uint value) => (value + (RecordAlign - 1)) & ~((uint)RecordAlign - 1);
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BlobchegHeader // exactly BlobchegFormat.HeaderSize bytes
    {
        public uint Magic;
        public ushort Version;
        public ushort Flags;

        public uint FileLength; // transport validation

        public uint DebugOffset; // absolute; 0 means none

        public ulong ContentHash; // xxHash3 of everything past the header, always checked

        public ulong NameHash; // name identity: swapped files each pass their own integrity check

        public bool HasDebug => (Flags & BlobchegFormat.FlagHasDebug) != 0;

        public bool IsRouter => (Flags & BlobchegFormat.FlagRouter) != 0;

        public BlobchegFileKind Kind => BlobchegFormat.KindOf(Flags);

        // Once per base load, so no define: any mismatch throws rather than half-loading a base
        public void Validate(string what, int actualLength, ulong actualContentHash,
            BlobchegFileKind wantKind = BlobchegFileKind.Database)
        {
            if (Magic != BlobchegFormat.Magic)
                throw new InvalidOperationException(
                    $"Blobcheg: '{what}' is not a blobcheg file (magic {Magic:X8}, expected {BlobchegFormat.Magic:X8})");

            if (Version != BlobchegFormat.Version)
                throw new InvalidOperationException(
                    $"Blobcheg: '{what}' is format version {Version}, the reader understands {BlobchegFormat.Version}");

            var kind = Kind;
            if (kind != wantKind)
                throw new InvalidOperationException(
                    $"Blobcheg: '{what}' is a {BlobchegFormat.NameOf(kind)} file, but it is being loaded as a " +
                    $"{BlobchegFormat.TargetOf(wantKind)}");

            var wantedName = BlobchegNaming.NameHash(what);
            if (NameHash != wantedName)
                throw new InvalidOperationException(
                    $"Blobcheg: '{what}' is the file of {BlobchegFormat.OwnerOf(kind)} " +
                    $"(the header says {NameHash:X16}, '{what}' is {wantedName:X16}). The files are swapped " +
                    "with each other or were rebuilt under different names");

            // Transient: a rebuild can swap the file between the length and body reads; next frame passes
            if (FileLength != (uint)actualLength)
                throw new BlobchegTransientException(
                    $"Blobcheg: '{what}' is truncated or extended: the header says {FileLength} B, {actualLength} B were read");

            if (DebugOffset != 0 && (DebugOffset < BlobchegFormat.HeaderSize || DebugOffset >= FileLength))
                throw new InvalidOperationException(
                    $"Blobcheg: '{what}' has its debug section at the impossible offset {DebugOffset} for a length of {FileLength}");

            if (ContentHash != actualContentHash)
                throw new InvalidOperationException(
                    $"Blobcheg: '{what}' failed the integrity check: the header says {ContentHash:X16}, {actualContentHash:X16} was computed");
        }
    }
}

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Blobcheg
{
    /// <summary>
    /// What kind of file lies in front of the reader. The kind is written as flags in the header and
    /// checked on load: otherwise mixed-up files come up and quietly hand out someone else's bytes.
    /// </summary>
    public enum BlobchegFileKind
    {
        Database = 0,
        Router = 1,

        /// <summary>A table of name hashes — the file of the <c>Blobcheg.Hashes</c> build.</summary>
        Hashes = 2,
    }

    /// <summary>
    /// The binary format of a base, version 1. A file is a header and aligned bytes lying one after
    /// another; there are no tables in the release file, and the only thing that gives a record
    /// meaning is the offset the consumer kept.
    /// </summary>
    public static class BlobchegFormat
    {
        /// <summary>'BCHG' in the byte order of the file.</summary>
        public const uint Magic = 0x47484342;

        /// <summary>
        /// 2 — the router appeared, 3 — the file gained an identity (the name hash in the header),
        /// 4 — there are now three file kinds and a single bool no longer tells them apart. The
        /// version is shared by every file of the package: they are derived and rebuilt together, and
        /// separate versions for the base and the router would be one more axis to fall out of sync
        /// along.
        /// </summary>
        public const ushort Version = 4;

        /// <summary>The size of the header and at the same time the offset of the first record.</summary>
        public const int HeaderSize = 32;

        /// <summary>The start of every record is aligned to this from the beginning of the file.</summary>
        public const int RecordAlign = 16;

        /// <summary>A flags bit: the file has a debug section.</summary>
        public const ushort FlagHasDebug = 1 << 0;

        /// <summary>A flags bit: the file is a router, not a base. Mixed-up ones are rejected on load.</summary>
        public const ushort FlagRouter = 1 << 1;

        /// <summary>A flags bit: the file is a hash table.</summary>
        public const ushort FlagHashes = 1 << 2;

        /// <summary>The file-kind flags — what the writer puts into the header and the reader checks.</summary>
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

        /// <summary>
        /// The file kind from the header flags. Two kinds at once means a corrupted file or one built
        /// by a foreign tool, and it is not "a base by default" but an error.
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
                        $"Blobcheg: the header claims two file kinds at once (flags {flags:X4})");
            }
        }

        /// <summary>"this is a ... file" — whose file lies in front of the reader.</summary>
        public static string NameOf(BlobchegFileKind kind)
        {
            switch (kind)
            {
                case BlobchegFileKind.Router: return "router";
                case BlobchegFileKind.Hashes: return "hash table";
                default: return "base";
            }
        }

        /// <summary>"it is being loaded as a ..." — what the reader takes it for.</summary>
        public static string TargetOf(BlobchegFileKind kind)
        {
            switch (kind)
            {
                case BlobchegFileKind.Router: return "router";
                case BlobchegFileKind.Hashes: return "hash table";
                default: return "base";
            }
        }

        /// <summary>"this is the file of another ..." — files of one kind swapped with each other.</summary>
        public static string OwnerOf(BlobchegFileKind kind)
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

    /// <summary>The start of a base file. Exactly <see cref="BlobchegFormat.HeaderSize"/> bytes.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct BlobchegHeader
    {
        public uint Magic;
        public ushort Version;
        public ushort Flags;

        /// <summary>The full length of the file — validation of the transport.</summary>
        public uint FileLength;

        /// <summary>The absolute offset of the debug section, 0 means there is none.</summary>
        public uint DebugOffset;

        /// <summary>xxHash3 of everything past the header. Integrity, always checked, behind no define.</summary>
        public ulong ContentHash;

        /// <summary>
        /// The identity of the file: <see cref="BlobchegNaming.NameHash"/> of the domain or router
        /// name. Without it two .bcheg files swapped with each other both come up and quietly hand out
        /// someone else's bytes — each has its own integrity and each adds up.
        /// </summary>
        public ulong NameHash;

        public bool HasDebug => (Flags & BlobchegFormat.FlagHasDebug) != 0;

        public bool IsRouter => (Flags & BlobchegFormat.FlagRouter) != 0;

        /// <summary>The file kind from the flags. Corrupted flags throw rather than return "a base".</summary>
        public BlobchegFileKind Kind => BlobchegFormat.KindOf(Flags);

        /// <summary>
        /// The check performed when a base is loaded. Not a hot path — called once per base, which is
        /// why it sits behind no define. Any discrepancy throws: either the base came up whole, or the
        /// game did not start.
        /// </summary>
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

            // Transient: the reader learns the length before the body, and between those two reads a
            // rebuild has time to swap the file — the header already belongs to the new one, the bytes
            // still to the old one. A frame later the same read goes through, see
            // BlobchegTransientException.
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

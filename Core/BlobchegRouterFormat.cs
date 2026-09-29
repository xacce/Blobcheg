using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace Blobcheg
{
    // Array offsets live in the prolog, so a layout change fails an old reader instead of misreading.
    public static class BlobchegRouterFormat
    {
        public const int PrologOffset = BlobchegFormat.HeaderSize;

        public const int PrologSize = 32;

        public const uint DebugMagic = 0x47445242; // 'BRDG'

        public const int MaxDomains = 64; // more bases in one router means a badly sliced project

        public static int MaskWidthFor(int domainCount)
        {
            if (domainCount <= 0)
                throw new ArgumentOutOfRangeException(nameof(domainCount),
                    "Blobcheg: a router without a single base — there is nothing to route");

            if (domainCount <= 8)
                return 1;
            if (domainCount <= 16)
                return 2;
            if (domainCount <= 32)
                return 4;
            if (domainCount <= MaxDomains)
                return 8;

            throw new ArgumentOutOfRangeException(nameof(domainCount),
                $"Blobcheg: {domainCount} bases in one router, the ceiling is {MaxDomains}");
        }

        // Proves codegen and editor build agree on bit numbering; duplicated in the generator, edit both.
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

    [StructLayout(LayoutKind.Sequential)]
    public struct BlobchegRouterProlog // exactly BlobchegRouterFormat.PrologSize bytes
    {
        public uint Count; // also the row ceiling of a valid id

        public uint DomainCount;

        public ulong LayoutHash;

        public uint MasksOffset;
        public uint RowStartOffset;
        public uint OffsetsOffset;

        public uint MaskWidth;

        // Bounds checked against file length: a broken prolog would read foreign memory on first Get.
        public void Validate(string what, int fileLength, int domainCount, ulong layoutHash)
        {
            if (LayoutHash != layoutHash)
                throw new InvalidOperationException(
                    $"Blobcheg: router '{what}' was built for a different set of bases (the file says {LayoutHash:X16}, " +
                    $"the code says {layoutHash:X16}) — rebuild the bases or build the code");

            if (DomainCount != (uint)domainCount)
                throw new InvalidOperationException(
                    $"Blobcheg: router '{what}' — the file holds {DomainCount} bases, the code holds {domainCount}");

            if (MaskWidth != (uint)BlobchegRouterFormat.MaskWidthFor(domainCount))
                throw new InvalidOperationException(
                    $"Blobcheg: router '{what}' — a mask width of {MaskWidth} B does not answer {domainCount} bases");

            var masksEnd = (long)MasksOffset + (long)Count * MaskWidth;
            var rowStartEnd = (long)RowStartOffset + ((long)Count + 1) * 4;

            if (MasksOffset < BlobchegRouterFormat.PrologOffset + BlobchegRouterFormat.PrologSize
                || masksEnd > fileLength
                || RowStartOffset < masksEnd
                || rowStartEnd > fileLength
                || OffsetsOffset < rowStartEnd
                || OffsetsOffset > fileLength)
                throw new InvalidOperationException(
                    $"Blobcheg: router '{what}' — the prolog points past a file of {fileLength} B");
        }
    }
}

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace Blobcheg
{
    /// <summary>
    /// The format of a router file. The same header as a base, plus a prolog and three arrays: the
    /// mask of a row, the start of its offsets and the offsets themselves. A row is a node, a bit is a
    /// base, the offsets are packed one after another, with no holes for bases that hold no node.
    ///
    /// The reader does NOT compute the layout: the array offsets lie in the prolog. Otherwise a change
    /// of layout would quietly send an old reader 16 bytes off instead of failing honestly on the
    /// version.
    /// </summary>
    public static class BlobchegRouterFormat
    {
        /// <summary>The prolog follows immediately after the header.</summary>
        public const int PrologOffset = BlobchegFormat.HeaderSize;

        public const int PrologSize = 32;

        /// <summary>'BRDG' in the byte order of the file — the prolog of the router debug section.</summary>
        public const uint DebugMagic = 0x47445242;

        /// <summary>More than 64 bases in one router is not "too few bits" but a badly sliced project.</summary>
        public const int MaxDomains = 64;

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

        /// <summary>
        /// The only thing that ties the bit numbering of the codegen to the one of the editor build:
        /// they arrive at it independently, so the fact that they agree is proven by a hash, not by
        /// word of honour. The algorithm is duplicated in the generator — change only in pairs.
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

    /// <summary>The prolog of a router file. Exactly <see cref="BlobchegRouterFormat.PrologSize"/> bytes.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct BlobchegRouterProlog
    {
        /// <summary>Rows, that is, nodes of the router. Also the ceiling of the row number in a valid id.</summary>
        public uint Count;

        public uint DomainCount;

        /// <summary>The hash of the bit numbering: the file and the codegen are obliged to agree.</summary>
        public ulong LayoutHash;

        public uint MasksOffset;
        public uint RowStartOffset;
        public uint OffsetsOffset;

        /// <summary>1, 2, 4 or 8 bytes per mask — according to the number of bases.</summary>
        public uint MaskWidth;

        /// <summary>
        /// The check performed on load, not a hot path. Array bounds are checked against the file
        /// length: otherwise a broken prolog would send the read into foreign memory on the very first
        /// <c>Get</c>.
        /// </summary>
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

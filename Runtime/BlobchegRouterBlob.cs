using System;
using System.Runtime.CompilerServices;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;

namespace Blobcheg
{
    /// <summary>
    /// A router row: one node across all bases at once. The mask says which bases it is in, the offsets
    /// lie one after another with no holes — hence <c>flag → index</c> is the popcount of the lower
    /// bits.
    ///
    /// The exception messages are literals: interpolation does not compile under Burst.
    /// </summary>
    public readonly unsafe struct BlobchegRouterRow
    {
        // A pointer into someone else's buffer: a row lives exactly as long as the loaded router.
        [NativeDisableUnsafePtrRestriction]
        readonly uint* _offsets;

        readonly ulong _mask;

        internal BlobchegRouterRow(uint* offsets, ulong mask)
        {
            _offsets = offsets;
            _mask = mask;
        }

        /// <summary>The bit mask of the bases the node is in. The codegen hands it out as its own enum.</summary>
        public ulong Mask => _mask;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Has(int bit) => (_mask & (1ul << bit)) != 0;

        /// <summary>
        /// The offset of the record in base <paramref name="bit"/>. If there is no record it throws:
        /// there is no "no record" sentinel in the package, and a silent zero would travel into
        /// <c>Read</c> and land in someone else's bytes.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public uint Offset(int bit)
        {
            if (!Has(bit))
                throw new InvalidOperationException(
                    "Blobcheg.Router: this node has no record in this base — ask Has or TryGet");

            return _offsets[math.countbits(_mask & ((1ul << bit) - 1))];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryOffset(int bit, out uint offset)
        {
            if (!Has(bit))
            {
                offset = 0;
                return false;
            }

            offset = _offsets[math.countbits(_mask & ((1ul << bit) - 1))];
            return true;
        }
    }

    /// <summary>
    /// The resident buffer of a router. It does all the work; the typed facade (the
    /// <c>[BlobchegRouter]</c> partial) is a thin wrapper on top that knows the bit numbers of its
    /// bases.
    /// </summary>
    public unsafe struct BlobchegRouterBlob : IDisposable
    {
        BlobchegBuffer _buffer;

        // Three pointers into that same immutable buffer. The attribute sits here and not on the
        // reader: a router enters a job as a field, and without it the safety system kills the schedule
        // over a raw pointer — naming a field of the package the consumer has no business with. Safe by
        // construction: the buffer lives for the whole session and is only read.
        [NativeDisableUnsafePtrRestriction]
        byte* _masks;

        [NativeDisableUnsafePtrRestriction]
        uint* _rowStart;

        [NativeDisableUnsafePtrRestriction]
        uint* _offsets;

        uint _count;
        uint _maskWidth;
        uint _debugOffset;
        byte _tag;

        /// <summary>Takes ownership of the buffer, validates the header, the integrity and the prolog.</summary>
        public BlobchegRouterBlob(BlobchegBuffer buffer, string what, int domainCount, ulong layoutHash)
        {
            if (!buffer.IsCreated)
                throw new ArgumentException($"Blobcheg: an empty buffer for router '{what}'", nameof(buffer));

            _buffer = buffer;
            _debugOffset = 0;
            _tag = BlobchegNaming.TagOf(what);

            ref var header = ref UnsafeUtility.AsRef<BlobchegHeader>(buffer.Ptr);
            var contentHash = BlobchegHash.Of(
                buffer.Ptr + BlobchegFormat.HeaderSize, buffer.Length - BlobchegFormat.HeaderSize);

            header.Validate(what, buffer.Length, contentHash, BlobchegFileKind.Router);

            if (buffer.Length < BlobchegRouterFormat.PrologOffset + BlobchegRouterFormat.PrologSize)
                throw new InvalidOperationException($"Blobcheg: router '{what}' is shorter than the prolog");

            ref var prolog = ref UnsafeUtility.AsRef<BlobchegRouterProlog>(buffer.Ptr + BlobchegRouterFormat.PrologOffset);
            prolog.Validate(what, buffer.Length, domainCount, layoutHash);

            _count = prolog.Count;
            _maskWidth = prolog.MaskWidth;
            _masks = buffer.Ptr + prolog.MasksOffset;
            _rowStart = (uint*)(buffer.Ptr + prolog.RowStartOffset);
            _offsets = (uint*)(buffer.Ptr + prolog.OffsetsOffset);

            if (header.HasDebug)
            {
                if (*(uint*)(buffer.Ptr + header.DebugOffset) != BlobchegRouterFormat.DebugMagic)
                    throw new InvalidOperationException(
                        $"Blobcheg: router '{what}' — the debug section is not where the header promised");

                _debugOffset = header.DebugOffset;
            }
        }

        public bool IsCreated => _buffer.IsCreated;

        /// <summary>How many rows, that is, nodes. Also the ceiling of the row number in a valid id.</summary>
        public int Count => (int)_count;

        public bool HasDebug => _debugOffset != 0;

        /// <summary>The tag of this router — the high byte of the ids it hands out.</summary>
        public byte Tag => _tag;

        /// <summary>
        /// The id of a row by its number. The range is NOT checked here — that is <see cref="Get"/>'s
        /// business; the path of tools and tests, a consumer does not assemble ids.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public BlobchegId IdAt(uint index) => BlobchegId.Make(_tag, index);

        /// <summary>
        /// The row of a node. Neither check sits behind a define: they are two comparisons, and a
        /// foreign or stale id would read foreign memory in a build.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public BlobchegRouterRow Get(BlobchegId id)
        {
            if (id.Tag != _tag)
                throw new InvalidOperationException(
                    "Blobcheg.Router: this id was handed out by another router — here it means nothing");

            if (id.Index >= _count)
                throw new InvalidOperationException(
                    "Blobcheg.Router: unknown id — the router has no row with that number");

            return new BlobchegRouterRow(_offsets + _rowStart[id.Index], MaskOf(id.Index));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryGet(BlobchegId id, out BlobchegRouterRow row)
        {
            if (id.Tag != _tag || id.Index >= _count)
            {
                row = default;
                return false;
            }

            row = new BlobchegRouterRow(_offsets + _rowStart[id.Index], MaskOf(id.Index));
            return true;
        }

        public void Dispose()
        {
            _buffer.Dispose();
            _masks = null;
            _rowStart = null;
            _offsets = null;
            _count = 0;
            _debugOffset = 0;
        }

        /// <summary>The node name by id — for editor tools only; a release player carries no section.</summary>
        public string Describe(BlobchegId id)
        {
            if (_debugOffset == 0)
                throw new InvalidOperationException(
                    "Blobcheg.Router.Describe: the file carries no debug contour — it was assembled for a release player");

            if (id.Tag != _tag || id.Index >= _count)
                throw new InvalidOperationException($"Blobcheg.Router.Describe: id {id} with {_count} rows");

            var nameOffset = *(uint*)(_buffer.Ptr + _debugOffset + 8 + id.Index * 4);
            var p = _buffer.Ptr + nameOffset;
            var length = *(ushort*)p;
            return System.Text.Encoding.UTF8.GetString(p + 2, length);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        ulong MaskOf(uint id)
        {
            switch (_maskWidth)
            {
                case 1: return _masks[id];
                case 2: return *(ushort*)(_masks + id * 2);
                case 4: return *(uint*)(_masks + id * 4);
                default: return *(ulong*)(_masks + id * 8);
            }
        }
    }
}

using System;
using System.Runtime.CompilerServices;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;

namespace Blobcheg
{
    // Offsets are packed with no holes, so a bit's index is the popcount of the lower mask bits.
    public readonly unsafe struct BlobchegRouterRow
    {
        [NativeDisableUnsafePtrRestriction]
        readonly uint* _offsets; // into the router buffer: lives exactly as long as the loaded router

        readonly ulong _mask;

        internal BlobchegRouterRow(uint* offsets, ulong mask)
        {
            _offsets = offsets;
            _mask = mask;
        }

        public ulong Mask => _mask;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Has(int bit) => (_mask & (1ul << bit)) != 0;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public uint Offset(int bit) // throws: a silent zero would reach Read and land in foreign bytes
        {
            if (!Has(bit))
                throw new InvalidOperationException(
                    "Blobcheg.Router: this node has no record in this base — ask Has or TryGet"); // literal: Burst

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

    public unsafe struct BlobchegRouterBlob : IDisposable
    {
        BlobchegBuffer _buffer;

        // Attribute here, not on the reader: a router enters jobs as a field; the buffer is read-only.
        [NativeDisableUnsafePtrRestriction]
        byte* _masks;

        [NativeDisableUnsafePtrRestriction]
        uint* _rowStart;

        [NativeDisableUnsafePtrRestriction]
        uint* _offsets;

        uint _count;
        uint _maskWidth;
        uint _debugOffset;
        ulong _identityKey;
        byte _tag;

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

            // The register is how Resident finds the router past any world, as the bases do.
            _identityKey = BlobchegNaming.NameHash(what);
            BlobchegDomainNames.Remember(_identityKey, what);
            BlobchegBases.Register(_identityKey, buffer.Ptr, buffer.Length, _debugOffset);
        }

        public static BlobchegRouterBlob FromRegistry(byte* ptr, int length, byte tag, uint debugOffset) // non-owning
        {
            ref var prolog = ref UnsafeUtility.AsRef<BlobchegRouterProlog>(ptr + BlobchegRouterFormat.PrologOffset);

            return new BlobchegRouterBlob
            {
                _buffer = new BlobchegBuffer { Ptr = ptr, Length = length, Allocator = Allocator.None },
                _tag = tag,
                _count = prolog.Count,
                _maskWidth = prolog.MaskWidth,
                _masks = ptr + prolog.MasksOffset,
                _rowStart = (uint*)(ptr + prolog.RowStartOffset),
                _offsets = (uint*)(ptr + prolog.OffsetsOffset),
                _debugOffset = debugOffset,
            };
        }

        public bool IsCreated => _buffer.IsCreated;

        public int Count => (int)_count; // also the row-number ceiling of a valid id

        public bool HasDebug => _debugOffset != 0;

        public byte Tag => _tag; // the high byte of the ids it hands out

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public BlobchegId IdAt(uint index) => BlobchegId.Make(_tag, index); // unchecked: tools and tests

        // Checks are not behind a define: a foreign or stale id would read foreign memory in a build.
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
            if (_identityKey != 0)
            {
                BlobchegBases.Unregister(_identityKey, _buffer.Ptr);
                _identityKey = 0;
            }

            _buffer.Dispose();
            _masks = null;
            _rowStart = null;
            _offsets = null;
            _count = 0;
            _debugOffset = 0;
        }

        public string Describe(BlobchegId id) // editor tools only: a release file has no debug section
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

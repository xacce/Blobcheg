using System;
using System.Runtime.CompilerServices;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

namespace Blobcheg
{
    // Resident hash table precomputed by the rebuild: loading only validates and wires pointers.
    public unsafe struct BlobchegHashesBlob : IDisposable
    {
        BlobchegBuffer _buffer;
        ulong* _keys;
        uint* _rows;
        ulong* _rowHash;
        uint* _backIndex;
        uint* _backOffsets;
        uint* _backRows;
        uint _count;
        uint _capacity;
        uint _domainCount;
        ulong _identityKey;
        byte _tag;

        public BlobchegHashesBlob(BlobchegBuffer buffer, string what, string routerName,
            int domainCount, ulong layoutHash)
        {
            if (!buffer.IsCreated)
                throw new ArgumentException($"Blobcheg: an empty buffer for table '{what}'", nameof(buffer));

            _buffer = buffer;
            _tag = BlobchegNaming.TagOf(routerName); // Tag from the router name; `what` is the file identity.

            ref var header = ref UnsafeUtility.AsRef<BlobchegHeader>(buffer.Ptr);
            var contentHash = BlobchegHash.Of(
                buffer.Ptr + BlobchegFormat.HeaderSize, buffer.Length - BlobchegFormat.HeaderSize);

            header.Validate(what, buffer.Length, contentHash, BlobchegFileKind.Hashes);

            if (buffer.Length < BlobchegHashesFormat.PrologOffset + BlobchegHashesFormat.PrologSize)
                throw new InvalidOperationException($"Blobcheg: table '{what}' is shorter than the prolog");

            ref var prolog = ref UnsafeUtility.AsRef<BlobchegHashesProlog>(
                buffer.Ptr + BlobchegHashesFormat.PrologOffset);

            prolog.Validate(what, buffer.Length, domainCount, layoutHash);

            _count = prolog.Count;
            _capacity = prolog.Capacity;
            _domainCount = prolog.DomainCount;
            _keys = (ulong*)(buffer.Ptr + prolog.KeysOffset);
            _rows = (uint*)(buffer.Ptr + prolog.RowsOffset);
            _rowHash = (ulong*)(buffer.Ptr + prolog.RowHashOffset);
            _backIndex = (uint*)(buffer.Ptr + prolog.BackIndexOffset);
            _backOffsets = (uint*)(buffer.Ptr + prolog.BackOffsetsOffset);
            _backRows = (uint*)(buffer.Ptr + prolog.BackRowsOffset);

            // Lane bounds must match the prolog total, else the file came from another writer.
            if (_backIndex[_domainCount] != prolog.Total)
                throw new InvalidOperationException(
                    $"Blobcheg: table '{what}' — the bounds of the reverse lanes do not agree with their length");

            _identityKey = BlobchegNaming.NameHash(what);
            BlobchegDomainNames.Remember(_identityKey, what);
            BlobchegBases.Register(_identityKey, buffer.Ptr, buffer.Length); // Lets Resident find the table past any world, as the bases do.
        }

        public static BlobchegHashesBlob FromRegistry(byte* ptr, int length, byte tag) // Non-owning: never validated, never disposed.
        {
            ref var prolog = ref UnsafeUtility.AsRef<BlobchegHashesProlog>(ptr + BlobchegHashesFormat.PrologOffset);

            return new BlobchegHashesBlob
            {
                _buffer = new BlobchegBuffer { Ptr = ptr, Length = length, Allocator = Allocator.None },
                _tag = tag,
                _count = prolog.Count,
                _capacity = prolog.Capacity,
                _domainCount = prolog.DomainCount,
                _keys = (ulong*)(ptr + prolog.KeysOffset),
                _rows = (uint*)(ptr + prolog.RowsOffset),
                _rowHash = (ulong*)(ptr + prolog.RowHashOffset),
                _backIndex = (uint*)(ptr + prolog.BackIndexOffset),
                _backOffsets = (uint*)(ptr + prolog.BackOffsetsOffset),
                _backRows = (uint*)(ptr + prolog.BackRowsOffset),
            };
        }

        public bool IsCreated => _buffer.IsCreated;

        public int Count => (int)_count; // Includes holes left by deleted nodes.

        public byte Tag => _tag; // High byte of the ids this table hands out.

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryGetRow(ulong hash, out uint row)
        {
            if (hash == 0) // Zero marks an empty slot, never a real hash.
            {
                row = 0;
                return false;
            }

            var slot = (uint)hash & (_capacity - 1);

            while (true)
            {
                var key = _keys[slot];

                if (key == hash)
                {
                    row = _rows[slot];
                    return true;
                }

                if (key == 0)
                {
                    row = 0;
                    return false;
                }

                slot = (slot + 1) & (_capacity - 1);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public uint GetRow(ulong hash)
        {
            if (!TryGetRow(hash, out var row))
                throw new InvalidOperationException(
                    "Blobcheg.Hashes: unknown hash — this router has no node with that name");

            return row;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ulong HashOfRow(uint row)
        {
            if (row >= _count)
                throw new InvalidOperationException(
                    "Blobcheg.Hashes: the table has no row with that number");

            return _rowHash[row]; // Zero for a deleted node's hole.
        }

        // Save path, not hot: binary search over the offset-sorted lane.
        public bool TryHashOfOffset(int bit, uint offset, out ulong hash)
        {
            if (bit < 0 || (uint)bit >= _domainCount)
                throw new InvalidOperationException(
                    "Blobcheg.Hashes: the base number is outside the router");

            var start = _backIndex[bit];
            var end = _backIndex[bit + 1];

            while (start < end)
            {
                var mid = start + (end - start) / 2;
                var at = _backOffsets[mid];

                if (at == offset)
                {
                    hash = _rowHash[_backRows[mid]];
                    return true;
                }

                if (at < offset)
                    start = mid + 1;
                else
                    end = mid;
            }

            hash = 0;
            return false;
        }

        public void Dispose()
        {
            if (_identityKey != 0)
            {
                BlobchegBases.Unregister(_identityKey, _buffer.Ptr);
                _identityKey = 0;
            }

            _buffer.Dispose();
            _keys = null;
            _rows = null;
            _rowHash = null;
            _backIndex = null;
            _backOffsets = null;
            _backRows = null;
            _count = 0;
            _capacity = 0;
            _domainCount = 0;
        }
    }
}

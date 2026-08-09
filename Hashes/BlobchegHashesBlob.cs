using System;
using System.Runtime.CompilerServices;
using Unity.Collections.LowLevel.Unsafe;

namespace Blobcheg
{
    /// <summary>
    /// The resident hash table. It does all the work; the typed facade (the
    /// <c>[BlobchegHashes]</c> partial) is a thin wrapper on top that knows the bit numbers of its
    /// bases.
    ///
    /// Loading it is a check of the header, the integrity and the prolog plus six pointers. Not a
    /// single insertion: the table was computed by the rebuild and lies in the file ready to use.
    ///
    /// The exception messages are literals: interpolation does not compile under Burst.
    /// </summary>
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
        byte _tag;

        /// <summary>
        /// Takes ownership of the buffer. The identity of the file is <paramref name="what"/> (the
        /// router name plus the suffix), while the tag for assembling a <see cref="BlobchegId"/> is
        /// computed from <paramref name="routerName"/>: these are different names, and both arrive as
        /// constants from the codegen.
        /// </summary>
        public BlobchegHashesBlob(BlobchegBuffer buffer, string what, string routerName,
            int domainCount, ulong layoutHash)
        {
            if (!buffer.IsCreated)
                throw new ArgumentException($"Blobcheg: an empty buffer for table '{what}'", nameof(buffer));

            _buffer = buffer;
            _tag = BlobchegNaming.TagOf(routerName);

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

            // The length of the lanes lies in the prolog and is obliged to agree with their own bounds:
            // if they disagree, the file was not assembled by this writer, and there is nothing further
            // to check.
            if (_backIndex[_domainCount] != prolog.Total)
                throw new InvalidOperationException(
                    $"Blobcheg: table '{what}' — the bounds of the reverse lanes do not agree with their length");
        }

        public bool IsCreated => _buffer.IsCreated;

        /// <summary>Rows, that is, nodes of the router, including the holes left by deleted ones.</summary>
        public int Count => (int)_count;

        /// <summary>The router tag — the high byte of the ids this table hands out.</summary>
        public byte Tag => _tag;

        /// <summary>
        /// The row number by hash. Zero is never a hash: it marks an empty slot, and asking for it means
        /// asking for "not assigned".
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryGetRow(ulong hash, out uint row)
        {
            if (hash == 0)
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

        /// <summary>The hash of a row by its number. A hole from a deleted node is zero.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ulong HashOfRow(uint row)
        {
            if (row >= _count)
                throw new InvalidOperationException(
                    "Blobcheg.Hashes: the table has no row with that number");

            return _rowHash[row];
        }

        /// <summary>
        /// The hash by the address of a record in base <paramref name="bit"/>. The save path, not a hot
        /// one: the lane is sorted by offset, the search is binary.
        /// </summary>
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

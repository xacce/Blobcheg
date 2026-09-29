using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

namespace Blobcheg
{
    // Integrity is checked once on load; bounds and type only under collection checks, else raw reads.
    public unsafe struct BlobchegBlob : IDisposable
    {
        BlobchegBuffer _buffer;
        uint _debugOffset;
        ulong _domainKey;

        public BlobchegBlob(BlobchegBuffer buffer, string what) // takes ownership of the buffer
        {
            if (!buffer.IsCreated)
                throw new ArgumentException($"Blobcheg: an empty buffer for base '{what}'", nameof(buffer));

            _buffer = buffer;
            _debugOffset = 0;
            _domainKey = 0;

            ref var header = ref UnsafeUtility.AsRef<BlobchegHeader>(buffer.Ptr);
            var contentHash = BlobchegHash.Of(
                buffer.Ptr + BlobchegFormat.HeaderSize, buffer.Length - BlobchegFormat.HeaderSize);

            header.Validate(what, buffer.Length, contentHash);

            if (header.HasDebug)
            {
                BlobchegDebugSection.ValidateProlog(*(uint*)(buffer.Ptr + header.DebugOffset));
                _debugOffset = header.DebugOffset;
            }

            _domainKey = header.NameHash;
            BlobchegDomainNames.Remember(_domainKey, what);
            BlobchegBases.Register(_domainKey, buffer.Ptr, buffer.Length, _debugOffset); // not in the patch: no Entities
        }

        public static BlobchegBlob FromRegistry(byte* ptr, int length, uint debugOffset, ulong domainKey) // non-owning view
            => new BlobchegBlob
            {
                _buffer = new BlobchegBuffer { Ptr = ptr, Length = length, Allocator = Allocator.None },
                _debugOffset = debugOffset,
                _domainKey = domainKey,
            };

        public ulong DomainKey => _domainKey;

        public bool IsCreated => _buffer.IsCreated;

        public int Length => _buffer.Length;

        public bool HasDebug => _debugOffset != 0;

        // Consumers keep the offset in a BlobchegRefSo and nowhere else.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref readonly T Read<T>(uint offset) where T : unmanaged
        {
            CheckRead<T>(offset);
            return ref UnsafeUtility.AsRef<T>(_buffer.Ptr + offset);
        }

        public void Dispose()
        {
            if (_domainKey != 0)
            {
                BlobchegBases.Unregister(_domainKey, _buffer.Ptr);
                _domainKey = 0;
            }

            _buffer.Dispose();
            _debugOffset = 0;
        }

        [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        void CheckRead<T>(uint offset) where T : unmanaged
        {
            if (_buffer.Ptr == null)
                throw new InvalidOperationException("Blobcheg.Read: the base is not loaded");

            if ((offset & (BlobchegFormat.RecordAlign - 1)) != 0)
                throw new InvalidOperationException("Blobcheg.Read: the offset is not aligned to 16 — this is not the start of a record");

            if (offset < BlobchegFormat.HeaderSize || offset + UnsafeUtility.SizeOf<T>() > (uint)_buffer.Length)
                throw new InvalidOperationException("Blobcheg.Read: the record does not fit into the base buffer");

            CheckType<T>(offset);
        }

        void CheckType<T>(uint offset) where T : unmanaged
        {
            if (_debugOffset == 0)
                return; // no debug section (release or foreign tool): nothing to check, not an error

            var entry = BlobchegDebugSection.Find(_buffer.Ptr, _debugOffset, offset);
            if (entry == null)
                throw new InvalidOperationException("Blobcheg.Read: there is no record at this offset");

            if (entry->TypeHash != unchecked((uint)BurstRuntime.GetHashCode32<T>()))
                throw new InvalidOperationException("Blobcheg.Read: a record of a different type lies at this offset");
        }

        // Editor tools only; ask after HasDebug, a missing section or record throws.
        public void Describe(uint offset, out string typeName, out string nodeName)
        {
            if (_debugOffset == 0)
                throw new InvalidOperationException(
                    "Blobcheg.Describe: the file carries no debug contour — it was assembled for a release player");

            var entry = BlobchegDebugSection.Find(_buffer.Ptr, _debugOffset, offset);
            if (entry == null)
                throw new InvalidOperationException($"Blobcheg.Describe: there is no record at offset {offset}");

            BlobchegDebugSection.ReadNames(_buffer.Ptr, *entry, out typeName, out nodeName);
        }
    }
}

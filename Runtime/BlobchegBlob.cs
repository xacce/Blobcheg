using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Unity.Burst;
using Unity.Collections.LowLevel.Unsafe;

namespace Blobcheg
{
    /// <summary>
    /// The resident buffer of one base. It does all the work; the typed facade (the
    /// <c>[Blobcheg]</c> partial) is a thin wrapper on top that adds the domain constraint.
    ///
    /// A read is a reinterpretation at an offset. What lies inside a record the buffer does not know
    /// and must not know: that is a question of trust. The integrity of the file and its identity are
    /// always checked (once, on load); the bounds and the record type sit behind
    /// <c>ENABLE_UNITY_COLLECTIONS_CHECKS</c>, that is, in the editor and in a development build. The
    /// type is checked against the debug contour, and a release player has none — there a read becomes
    /// a pure reinterpretation again.
    /// </summary>
    public unsafe struct BlobchegBlob : IDisposable
    {
        BlobchegBuffer _buffer;
        uint _debugOffset;
        ulong _domainKey;

        /// <summary>Takes ownership of the buffer, validates the header and the integrity.</summary>
        public BlobchegBlob(BlobchegBuffer buffer, string what)
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

            // The identity of the domain is already checked above, so the registry key is that same
            // identity. We register here and not in the patch: a base is loaded without Entities too,
            // and the question "is the slot an address or still an offset" is asked by everyone alike.
            _domainKey = header.NameHash;
            BlobchegDomainNames.Remember(_domainKey, what);
            BlobchegBases.Register(_domainKey, buffer.Ptr, buffer.Length, _debugOffset);
        }

        /// <summary>The domain key in <see cref="BlobchegBases"/> — the file identity from the header.</summary>
        public ulong DomainKey => _domainKey;

        public bool IsCreated => _buffer.IsCreated;

        public int Length => _buffer.Length;

        /// <summary>Whether the file carries a debug contour. A release build never has one.</summary>
        public bool HasDebug => _debugOffset != 0;

        /// <summary>
        /// The only way to get a record out. The consumer keeps the offset themselves — in a
        /// <see cref="BlobchegRefSo"/> and nowhere else.
        /// </summary>
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

        /// <summary>
        /// The debug contour: whether there is a record at this address and whether it is the right
        /// one. Called from <see cref="CheckRead{T}"/>, so it lives under the same
        /// <c>ENABLE_UNITY_COLLECTIONS_CHECKS</c> — in the editor and in a development build. It used
        /// to hang on a separate <c>BLOBCHEG_DEBUG</c> that nobody ever set, and the only type check
        /// existed on paper.
        ///
        /// The file may carry no section at all (a release build, a file assembled by a foreign tool) —
        /// then there is nothing to check against, and that is not a read error.
        /// </summary>
        void CheckType<T>(uint offset) where T : unmanaged
        {
            if (_debugOffset == 0)
                return;

            var entry = BlobchegDebugSection.Find(_buffer.Ptr, _debugOffset, offset);
            if (entry == null)
                throw new InvalidOperationException("Blobcheg.Read: there is no record at this offset");

            if (entry->TypeHash != unchecked((uint)BurstRuntime.GetHashCode32<T>()))
                throw new InvalidOperationException("Blobcheg.Read: a record of a different type lies at this offset");
        }

        /// <summary>
        /// Type and node names by offset — for editor tools only. It makes sense to ask after
        /// <see cref="HasDebug"/>; without a section or without a record at the offset it is an error,
        /// not an empty answer.
        /// </summary>
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

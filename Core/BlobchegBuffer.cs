using System;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

namespace Blobcheg
{
    /// <summary>
    /// The owning buffer of a base file: raw memory aligned to <see cref="BlobchegFormat.RecordAlign"/>.
    /// Deliberately not a <see cref="NativeArray{T}"/> — that one gives no alignment guarantee, and the
    /// one converted from a pointer drags along the safety-handle fuss for the sake of indexing that
    /// does not happen here: the buffer is entered by reinterpreting at an offset, not by index.
    /// </summary>
    public unsafe struct BlobchegBuffer : IDisposable
    {
        // Without this a job holding a base as a field does not schedule at all: the safety system
        // forbids raw pointers in jobs. Here it is safe by construction — the buffer is immutable
        // for the whole session and is only ever read, there are no races over it.
        [NativeDisableUnsafePtrRestriction]
        public byte* Ptr;
        public int Length;
        public Allocator Allocator;

        public bool IsCreated => Ptr != null;

        public static BlobchegBuffer Alloc(int length, Allocator allocator)
        {
            if (length < BlobchegFormat.HeaderSize)
                throw new ArgumentOutOfRangeException(nameof(length),
                    $"Blobcheg: a buffer of {length} B is shorter than the {BlobchegFormat.HeaderSize} B header");

            return new BlobchegBuffer
            {
                Ptr = (byte*)UnsafeUtility.Malloc(length, BlobchegFormat.RecordAlign, allocator),
                Length = length,
                Allocator = allocator,
            };
        }

        /// <summary>A copy of a managed array into an own aligned buffer — the editor and test path.</summary>
        public static BlobchegBuffer From(byte[] bytes, Allocator allocator)
        {
            var buffer = Alloc(bytes.Length, allocator);
            fixed (byte* src = bytes)
                UnsafeUtility.MemCpy(buffer.Ptr, src, bytes.Length);

            return buffer;
        }

        public void Dispose()
        {
            if (Ptr == null)
                return;

            UnsafeUtility.Free(Ptr, Allocator);
            Ptr = null;
            Length = 0;
        }
    }
}

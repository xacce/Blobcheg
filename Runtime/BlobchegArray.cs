using System;
using System.Diagnostics;
using Unity.Burst;
using Unity.Collections.LowLevel.Unsafe;

namespace Blobcheg
{
    /// <summary>
    /// A typed variable-length array inside a record: eight bytes — a self-relative offset and a
    /// length. The offset is measured from the address of THIS field, the tail lies inside the byte
    /// block of the same record, so the record stays an opaque block that travels through the file as a
    /// whole: neither Flush, nor integrity, nor the revision, nor the reference patch knows about the
    /// array.
    ///
    /// Every member is readonly on purpose. <see cref="BlobchegBlob.Read{T}"/> hands out a
    /// <c>ref readonly</c>, and access to a non-readonly member through such a reference is served by
    /// the compiler with a defensive copy — and a copy has a different address, so a self-relative
    /// offset taken from it leads nowhere, silently and on the normal path.
    ///
    /// Only the record builder in the editor fills the field. A zero offset means an empty array, and
    /// it is read without dereferencing.
    /// </summary>
    public unsafe struct BlobchegArray<T> where T : unmanaged
    {
        internal int _offset;   // bytes from the address of this field to the first element; 0 means empty
        internal int _length;

        public readonly int Length => _length;

        public readonly bool IsEmpty => _length == 0;

        public readonly ref readonly T this[int index]
        {
            get
            {
                fixed (int* self = &_offset)
                {
                    var element = (byte*)self + _offset + (long)index * sizeof(T);
                    CheckElement(index, element);
                    return ref *(T*)element;
                }
            }
        }

        /// <summary>
        /// A pointer to the first element — the shape for a hot loop: the address is checked once and
        /// the loop after that is free. An empty array has no pointer — <c>null</c> without
        /// dereferencing.
        /// </summary>
        public readonly T* GetUnsafePtr()
        {
            if (_length == 0)
                return null;

            fixed (int* self = &_offset)
            {
                var first = (byte*)self + _offset;
                CheckSpan(first, first + (long)_length * sizeof(T) - 1);
                return (T*)first;
            }
        }

        [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        readonly void CheckElement(int index, byte* element)
        {
            if ((uint)index >= (uint)_length)
                throw new IndexOutOfRangeException("Blobcheg: index past the bounds of the record array");

            CheckSpan(element, element + sizeof(T) - 1);
        }

        /// <summary>
        /// The first and the last byte of the span are obliged to lie in the buffer of some loaded
        /// base. Divisibility is checked on the absolute address of the element, not on the offset
        /// itself: the array field may sit at 4 inside a record while the element is 8 bytes wide, and
        /// then the offset is not divisible even though the element is aligned correctly — what matters
        /// is the address that is read from.
        /// </summary>
        [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        readonly void CheckSpan(byte* first, byte* last)
        {
            if (_offset == 0)
                throw new InvalidOperationException(
                    "Blobcheg: a non-empty array has a zero offset — the field was never filled by a record builder");

            if ((ulong)first % (ulong)UnsafeUtility.AlignOf<T>() != 0)
                throw new InvalidOperationException(
                    "Blobcheg: the element address is not a multiple of its type alignment — the array offset is broken");

            if (BlobchegBases.IsKnownAddress((ulong)first) && BlobchegBases.IsKnownAddress((ulong)last))
                return;

            ThrowCopied();
            throw new InvalidOperationException(
                "Blobcheg: the element address is outside the buffers of the loaded bases — the record was " +
                "copied out of the blob by value, and a self-relative offset only lives at the original " +
                "address. Hold the record as ref readonly, do not copy it into a local variable");
        }

        /// <summary>
        /// The managed version of the same error, carrying the element type name: this is the most
        /// frequent human mistake, and it must be visible without guessing. Under Burst the method is
        /// discarded — there the literal text above is what throws.
        /// </summary>
        [BurstDiscard]
        static void ThrowCopied()
            => throw new InvalidOperationException(
                $"Blobcheg: the array of '{typeof(T).FullName}' elements is read from a copy of the record — " +
                "the record was copied out of the blob by value, and a self-relative offset only lives at " +
                "the original address. Hold the record as ref readonly, do not copy it into a local " +
                "variable");
    }
}

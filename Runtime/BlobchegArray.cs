using System;
using System.Diagnostics;
using Unity.Burst;
using Unity.Collections.LowLevel.Unsafe;

namespace Blobcheg
{
    // Members stay readonly: a defensive copy via ref readonly would break the self-relative offset.
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

        public readonly T* GetUnsafePtr() // for hot loops: the span is checked once, not per element
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

        [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        readonly void CheckSpan(byte* first, byte* last)
        {
            if (_offset == 0)
                throw new InvalidOperationException(
                    "Blobcheg: a non-empty array has a zero offset — the field was never filled by a record builder");

            if ((ulong)first % (ulong)UnsafeUtility.AlignOf<T>() != 0) // the address, not the offset: field may sit at 4
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

        [BurstDiscard] // managed message naming T; under Burst the literal above throws
        static void ThrowCopied()
            => throw new InvalidOperationException(
                $"Blobcheg: the array of '{typeof(T).FullName}' elements is read from a copy of the record — " +
                "the record was copied out of the blob by value, and a self-relative offset only lives at " +
                "the original address. Hold the record as ref readonly, do not copy it into a local " +
                "variable");
    }
}

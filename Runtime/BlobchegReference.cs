using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Unity.Collections.LowLevel.Unsafe;

namespace Blobcheg
{
    // Eight bytes: the file offset before the patch, the loaded-buffer address after it.
    public struct BlobchegReferenceData // the field walk finds slots by this type; 0 = unassigned
    {
        public ulong Value;
    }

    public unsafe struct BlobchegReference<T> : IEquatable<BlobchegReference<T>> where T : unmanaged
    {
        public BlobchegReferenceData Data; // runtime slot in a component, unlike the editor-side BlobchegRef<T>

        public BlobchegReference(uint offset) => Data = new BlobchegReferenceData { Value = offset };

        public bool IsSet => Data.Value != 0;

        public bool IsResolved => Data.Value != 0 && BlobchegBases.IsKnownAddress(Data.Value); // unpatched is normal before the patch, not invalid

        public ref readonly T Value
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                CheckResolved();
                return ref UnsafeUtility.AsRef<T>((void*)Data.Value);
            }
        }

        // Compares the raw slot, so a == b answers the same before and after the patch.
        public bool Equals(BlobchegReference<T> other) => Data.Value == other.Data.Value;

        public override bool Equals(object obj) => obj is BlobchegReference<T> other && Equals(other);

        public override int GetHashCode() => Data.Value.GetHashCode();

        public static bool operator ==(BlobchegReference<T> a, BlobchegReference<T> b) => a.Equals(b);

        public static bool operator !=(BlobchegReference<T> a, BlobchegReference<T> b) => !a.Equals(b);

        [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        void CheckResolved()
        {
            if (Data.Value == 0)
                throw new InvalidOperationException(
                    $"Blobcheg: an empty BlobchegReference<{typeof(T).Name}> — no record is assigned");

            if (!BlobchegBases.IsKnownAddress(Data.Value))
                throw new InvalidOperationException(
                    $"Blobcheg: BlobchegReference<{typeof(T).Name}> is not patched — the slot holds offset {Data.Value}, " +
                    "not an address. The entity never went through the import patch, or the domain base is not loaded");
        }
    }

    public unsafe struct BlobchegRawReference // untyped, for AddBytes records: hands out bytes
    {
        public BlobchegReferenceData Data;

        public BlobchegRawReference(uint offset) => Data = new BlobchegReferenceData { Value = offset };

        public bool IsSet => Data.Value != 0;

        public bool IsResolved => Data.Value != 0 && BlobchegBases.IsKnownAddress(Data.Value);

        public byte* Ptr
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                CheckResolved();
                return (byte*)Data.Value;
            }
        }

        [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        void CheckResolved()
        {
            if (Data.Value == 0)
                throw new InvalidOperationException("Blobcheg: an empty BlobchegRawReference — no record is assigned");

            if (!BlobchegBases.IsKnownAddress(Data.Value))
                throw new InvalidOperationException(
                    $"Blobcheg: BlobchegRawReference is not patched — the slot holds offset {Data.Value}, not an address");
        }
    }
}

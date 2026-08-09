using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Unity.Collections.LowLevel.Unsafe;

namespace Blobcheg
{
    /// <summary>
    /// The slot of a reference to a record inside a component. Eight bytes, and two different things
    /// live in them in turn: before the patch the offset of the record in the file, after the patch its
    /// address in the loaded buffer. Unity's <c>BlobAssetReferenceData</c> is built exactly the same
    /// way and for the same reason: the serialisable form is obliged to outlive the process, while the
    /// read should happen without an addition.
    ///
    /// The type is separate and untyped because the field walk recognises our slots inside a foreign
    /// struct by it — by comparing the field type, not the name. Zero means "not assigned" for free:
    /// offsets start at <see cref="BlobchegFormat.HeaderSize"/>, and there is no such thing as a zero
    /// address.
    /// </summary>
    public struct BlobchegReferenceData
    {
        public ulong Value;
    }

    /// <summary>
    /// A reference to a record living inside an entity component. Put down by the baker as an offset,
    /// turned into an address by the patch on scene import; read without a base and without an
    /// addition.
    ///
    /// This is not a replacement for <see cref="BlobchegRef{T}"/>: that one is the editor field
    /// carrying an address, this one is the runtime slot in a component. The usual "offset plus
    /// <c>Read</c>" path is not going anywhere.
    /// </summary>
    public unsafe struct BlobchegReference<T> : IEquatable<BlobchegReference<T>> where T : unmanaged
    {
        public BlobchegReferenceData Data;

        /// <summary>From a record address in the editor: <c>new BlobchegReference&lt;GunData&gt;(a.gun.Offset)</c>.</summary>
        public BlobchegReference(uint offset) => Data = new BlobchegReferenceData { Value = offset };

        public bool IsSet => Data.Value != 0;

        /// <summary>
        /// Whether the field is patched. Not "valid": an unpatched field is the normal state of an
        /// entity that has not reached the patch yet.
        /// </summary>
        public bool IsResolved => Data.Value != 0 && BlobchegBases.IsKnownAddress(Data.Value);

        /// <summary>
        /// The record itself. In release a pure reinterpretation at the address; in the editor and in a
        /// development build it is checked that the slot really holds the address of a loaded base and
        /// not a leftover offset.
        /// </summary>
        public ref readonly T Value
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                CheckResolved();
                return ref UnsafeUtility.AsRef<T>((void*)Data.Value);
            }
        }

        /// <summary>
        /// Two references are equal if they hold the same thing. The comparison is obliged to answer the
        /// same way before and after the patch — otherwise the familiar <c>if (a == b)</c> in game code
        /// starts lying right after a scene load; both states are compared by the content of the slot,
        /// and both agree.
        /// </summary>
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

    /// <summary>
    /// The same without a parameter — for records from <c>AddBytes</c>, which have no type. It hands out
    /// bytes because there is nothing to reinterpret: the hole is in exactly the same place as in
    /// <see cref="BlobchegRawRef"/>.
    /// </summary>
    public unsafe struct BlobchegRawReference
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

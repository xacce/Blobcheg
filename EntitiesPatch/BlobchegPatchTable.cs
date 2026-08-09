using System;
using Unity.Burst;
using Unity.Collections.LowLevel.Unsafe;

namespace Blobcheg
{
    /// <summary>One slot in a component: where it lies and which domain its record comes from.</summary>
    public struct BlobchegFieldSlot
    {
        public int Offset;
        public ulong DomainKey;

        /// <summary>
        /// The identity of the record type — the same one the base writer puts into the debug contour.
        /// By it the patch checks that it reached its own record rather than the neighbouring one:
        /// without that check a shifted layout hands out someone else's bytes silently.
        /// </summary>
        public uint RecordTypeHash;
    }

    /// <summary>The stretch of the flat slot list that belongs to one component type.</summary>
    public struct BlobchegSlotRange
    {
        public int Start;
        public int Count;
    }

    /// <summary>
    /// Where the <see cref="BlobchegReference{T}"/> slots lie inside components. The same thing in
    /// meaning as Unity's <c>TypeInfo.BlobAssetRefOffsets</c>, only on the side: adding a fifth kind of
    /// offset into <c>TypeInfo</c> would mean editing the TypeManager reflection, the IL post-processor
    /// and the static type registry — for the sake of a table that lives its own life perfectly well.
    ///
    /// The key is a <c>TypeIndex</c>, because that is exactly what the patch has in hand: the chunk
    /// loop knows the archetype type, not <c>T</c>.
    ///
    /// There is not a single managed static in this type, and it must stay that way: Burst code reads
    /// it, and Burst drags the whole static constructor of the class along. The list of registered
    /// types and all the assembly reflection live in <see cref="BlobchegPatchTableBuilder"/> for exactly
    /// that reason.
    /// </summary>
    public static unsafe class BlobchegPatchTable
    {
        internal struct Data
        {
            public IntPtr Map;    // UnsafeHashMap<int, BlobchegSlotRange>*
            public IntPtr Slots;  // UnsafeList<BlobchegFieldSlot>*
        }

        static readonly SharedStatic<Data> s_Data = SharedStatic<Data>.GetOrCreate<Data>();

        public static bool IsBuilt => s_Data.Data.Map != IntPtr.Zero;

        internal static Data Storage
        {
            get => s_Data.Data;
            set => s_Data.Data = value;
        }

        /// <summary>
        /// The slots of a type. Called from Burst code, which is why it hands out a raw pointer and a
        /// count rather than a list.
        /// </summary>
        public static bool TryGetSlots(int typeIndex, out BlobchegFieldSlot* slots, out int count)
        {
            var data = s_Data.Data;
            if (data.Map == IntPtr.Zero)
            {
                slots = null;
                count = 0;
                return false;
            }

            var map = (UnsafeHashMap<int, BlobchegSlotRange>*)data.Map;
            if (!map->TryGetValue(typeIndex, out var range))
            {
                slots = null;
                count = 0;
                return false;
            }

            var all = (UnsafeList<BlobchegFieldSlot>*)data.Slots;
            slots = all->Ptr + range.Start;
            count = range.Count;
            return true;
        }
    }
}

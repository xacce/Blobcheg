using System;
using Unity.Burst;
using Unity.Collections.LowLevel.Unsafe;

namespace Blobcheg
{
    public struct BlobchegFieldSlot
    {
        public int Offset;
        public ulong DomainKey;

        public uint RecordTypeHash; // lets a shifted layout fail loudly instead of reading a neighbour
    }

    public struct BlobchegSlotRange
    {
        public int Start;
        public int Count;
    }

    // Side table of reference offsets by TypeIndex, read from Burst: no managed statics may live here.
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

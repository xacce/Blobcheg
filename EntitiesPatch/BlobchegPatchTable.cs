using System;
using Unity.Burst;
using Unity.Collections.LowLevel.Unsafe;

namespace Blobcheg
{
    /// <summary>Один слот в компоненте: где лежит и из какого домена его запись.</summary>
    public struct BlobchegFieldSlot
    {
        public int Offset;
        public ulong DomainKey;

        /// <summary>
        /// Личность типа записи — та же, что пишет в отладочный контур писатель базы. По ней патч
        /// сверяет, что доехал именно до своей записи, а не до соседней: без этой сверки переезд
        /// раскладки отдаёт чужие байты молча.
        /// </summary>
        public uint RecordTypeHash;
    }

    /// <summary>Участок плоского списка слотов, принадлежащий одному типу компонента.</summary>
    public struct BlobchegSlotRange
    {
        public int Start;
        public int Count;
    }

    /// <summary>
    /// Где в компонентах лежат слоты <see cref="BlobchegReference{T}"/>. То же по смыслу, что
    /// <c>TypeInfo.BlobAssetRefOffsets</c> у Unity, только сбоку: добавить пятый вид оффсета в
    /// <c>TypeInfo</c> значило бы править и рефлексию TypeManager, и IL-постпроцессор, и статический
    /// реестр типов — ради таблицы, которая прекрасно живёт своей жизнью.
    ///
    /// Ключ — <c>TypeIndex</c>, потому что в патче на руках именно он: чанк-цикл знает тип архетипа,
    /// а не <c>T</c>.
    ///
    /// В этом типе нет ни одного managed-статика, и так должно остаться: его читает Burst-код, а
    /// Бёрст тянет за собой весь статический конструктор класса. Список зарегистрированных типов и
    /// вся рефлексия сборки живут в <see cref="BlobchegPatchTableBuilder"/> именно поэтому.
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
        /// Слоты типа. Зовётся из Burst-кода, поэтому отдаёт сырой указатель и число, а не список.
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

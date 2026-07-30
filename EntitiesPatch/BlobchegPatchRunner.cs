using System;
using AOT;
using Unity.Burst;
using Unity.Entities;

namespace Blobcheg
{
    /// <summary>
    /// Сам патч. Зовётся форком на каждый непрерывный прогон элементов компонента: для обычного
    /// компонента — раз на тип в чанке, для буфера — раз на сущность.
    ///
    /// Burst-код, поэтому исключений здесь нет: сообщение с подставленными числами под Бёрстом не
    /// собрать. Провал кладётся в <see cref="BlobchegPatchErrors"/>, а человеку его показывает
    /// managed-сторона — на ближайшем апдейте бут-группы.
    /// </summary>
    [BurstCompile]
    internal static unsafe class BlobchegPatchRunner
    {
        public const int ModeResolve = 0;
        public const int ModeUnresolve = 1;

        [BurstCompile]
        [MonoPInvokeCallback(typeof(BlobchegPatchHook.PatchElements))]
        public static void PatchElements(int typeIndex, byte* elements, int elementCount, int elementStride, int mode)
        {
            if (elements == null || elementCount <= 0)
                return;

            if (!BlobchegPatchTable.TryGetSlots(typeIndex, out var slots, out var slotCount))
                return;

            for (var e = 0; e < elementCount; e++)
            {
                var element = elements + (long)e * elementStride;

                for (var i = 0; i < slotCount; i++)
                {
                    var slot = slots + i;
                    var cell = (ulong*)(element + slot->Offset);
                    var value = *cell;

                    if (value == 0)
                        continue;

                    var result = mode == ModeUnresolve
                        ? BlobchegBases.TryUnresolve(slot->DomainKey, value, out var moved)
                        : BlobchegBases.TryResolve(slot->DomainKey, value, out moved);

                    switch (result)
                    {
                        case BlobchegRebase.Patched:
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                            // Сверка ДО записи: провалившийся патч обязан оставить слот как был.
                            // Записав чужой адрес и только потом пожаловавшись, мы бы отравили поле —
                            // и следующий проход перевёл бы отраву как законный адрес поколения.
                            if (mode != ModeUnresolve && !RecordMatches(slot, moved))
                            {
                                BlobchegPatchErrors.Report(
                                    BlobchegRebase.WrongRecord, typeIndex, slot->DomainKey, moved);
                                break;
                            }
#endif
                            *cell = moved;
                            break;

                        case BlobchegRebase.Unchanged:
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                            if (mode != ModeUnresolve && !RecordMatches(slot, value))
                                BlobchegPatchErrors.Report(
                                    BlobchegRebase.WrongRecord, typeIndex, slot->DomainKey, value);
#endif
                            break;

                        default:
                            BlobchegPatchErrors.Report(result, typeIndex, slot->DomainKey, value);
                            break;
                    }
                }
            }
        }

        /// <summary>
        /// Сверяет, что по полученному адресу действительно начинается запись ожидаемого типа.
        /// Опирается на отладочный контур файла — тот самый, которым <c>BlobchegBlob.Read</c>
        /// проверяет тип на старом пути; в релизном плеере контура нет, и проверки тоже.
        ///
        /// Без неё две беды проходят молча: слот, типизированный близнецом записи, и переезд
        /// раскладки, после которого перевод поколения отдаёт соседнюю запись вместо своей.
        /// </summary>
        static bool RecordMatches(BlobchegFieldSlot* slot, ulong address)
        {
            if (slot->RecordTypeHash == 0)
                return true;

            if (!BlobchegBases.TryGetDebug(slot->DomainKey, out var basePtr, out var debugOffset))
                return true;

            // Контура нет — релизный файл, проверять нечем, и это не ошибка чтения.
            if (debugOffset == 0)
                return true;

            var offset = (uint)(address - (ulong)basePtr);
            var entry = BlobchegDebugSection.Find(basePtr, debugOffset, offset);

            return entry != null && entry->TypeHash == slot->RecordTypeHash;
        }
    }

    /// <summary>
    /// Почтовый ящик провалов патча: Burst-код кладёт сюда код и числа, managed-сторона собирает из
    /// них человеческое сообщение. Первый провал побеждает — остальные только считаются, иначе
    /// сцена на десять тысяч сущностей завалила бы лог одним и тем же.
    /// </summary>
    public static class BlobchegPatchErrors
    {
        internal struct Slot
        {
            public byte Code;
            public int TypeIndex;
            public ulong DomainKey;
            public ulong Value;
            public int Count;
        }

        static readonly SharedStatic<Slot> s_Slot = SharedStatic<Slot>.GetOrCreate<Slot>();

        internal static void Report(BlobchegRebase code, int typeIndex, ulong domainKey, ulong value)
        {
            ref var slot = ref s_Slot.Data;

            if (slot.Count == 0)
            {
                slot.Code = (byte)code;
                slot.TypeIndex = typeIndex;
                slot.DomainKey = domainKey;
                slot.Value = value;
            }

            slot.Count++;
        }

        public static bool HasAny => s_Slot.Data.Count != 0;

        public static void Clear() => s_Slot.Data = default;

        /// <summary>
        /// Бросает по первому провалу и чистит ящик. Зовётся с managed-стороны, поэтому здесь и
        /// имя типа, и имя домена, и число повторов.
        /// </summary>
        public static void ThrowIfAny()
        {
            var slot = s_Slot.Data;
            if (slot.Count == 0)
                return;

            Clear();

            var component = ComponentName(slot.TypeIndex);
            var domain = BlobchegDomainNames.Of(slot.DomainKey);
            var more = slot.Count > 1 ? $" (и ещё {slot.Count - 1} таких же)" : string.Empty;

            switch ((BlobchegRebase)slot.Code)
            {
                case BlobchegRebase.DomainNotRaised:
                    throw new InvalidOperationException(
                        $"Blobcheg: сущности с '{component}' приехали раньше своей базы — домен " +
                        $"'{domain}' не поднят, патчить нечем{more}. Грузить сабсцены можно только " +
                        "после того, как выставлен синглтон готовности баз");

                case BlobchegRebase.BadOffset:
                    throw new InvalidOperationException(
                        $"Blobcheg: в '{component}' лежит {slot.Value} — как оффсет домена '{domain}' " +
                        $"это невозможно{more}: либо внутри header'а (первые " +
                        $"{BlobchegFormat.HeaderSize} Б), либо не кратно {BlobchegFormat.RecordAlign}. " +
                        "Начало записи выглядит не так");

                case BlobchegRebase.OutOfRange:
                    throw new InvalidOperationException(
                        $"Blobcheg: в '{component}' лежит {slot.Value} — это не оффсет домена " +
                        $"'{domain}' и не адрес живого поколения его буфера{more}. Похоже, сущность " +
                        "пережила пересборку базы, буфер которой уже освобождён");

                case BlobchegRebase.WrongRecord:
                    throw new InvalidOperationException(
                        $"Blobcheg: слот в '{component}' доехал до адреса {slot.Value} в домене " +
                        $"'{domain}', но по нему не начинается запись объявленного типа{more}. Либо " +
                        "слот типизирован не той записью, либо пересборка двинула раскладку и перевод " +
                        "поколения отдал соседнюю запись — сущности надо перепечь");

                default:
                    throw new InvalidOperationException(
                        $"Blobcheg: патч '{component}' провалился с кодом {slot.Code}, значение " +
                        $"{slot.Value}, домен '{domain}'{more}");
            }
        }

        /// <summary>
        /// Имя типа ищется по списку зарегистрированных, а не через TypeManager: сюда попадают
        /// только те типы, что таблица и завела, а обращаться к TypeManager из обработчика ошибки —
        /// лишний способ упасть по дороге к сообщению.
        /// </summary>
        static string ComponentName(int typeIndex)
        {
            foreach (var type in BlobchegPatchTableBuilder.RegisteredTypes)
                if (type.TypeIndex.Value == typeIndex)
                    return type.GetManagedType()?.Name ?? $"тип #{typeIndex}";

            return $"тип #{typeIndex}";
        }
    }
}

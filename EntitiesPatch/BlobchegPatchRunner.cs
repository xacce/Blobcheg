using System;
using AOT;
using Unity.Burst;
using Unity.Entities;

namespace Blobcheg
{
    // Burst patch per contiguous element run; failures go to BlobchegPatchErrors since Burst cannot throw.
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
                            // Check before writing: a failed patch must leave the slot intact, else the next pass trusts it.
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

        // Debug-contour type check: catches a slot typed with a twin record or a shifted layout.
        static bool RecordMatches(BlobchegFieldSlot* slot, ulong address)
        {
            if (slot->RecordTypeHash == 0)
                return true;

            if (!BlobchegBases.TryGetDebug(slot->DomainKey, out var basePtr, out var debugOffset))
                return true;

            if (debugOffset == 0) // Release file: no contour, nothing to check.
                return true;

            var offset = (uint)(address - (ulong)basePtr);
            var entry = BlobchegDebugSection.Find(basePtr, debugOffset, offset);

            return entry != null && entry->TypeHash == slot->RecordTypeHash;
        }
    }

    public static class BlobchegPatchErrors
    {
        internal struct Slot
        {
            public byte Code;
            public int TypeIndex;
            public ulong DomainKey;
            public ulong Value;
            public int Count; // First failure wins; later ones are only counted so a big scene cannot flood the log.
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

        // whileBasesRise forgives only DomainNotRaised: the slot stays an offset and the next pass fixes it.
        public static void ThrowIfAny(bool whileBasesRise = false)
        {
            var slot = s_Slot.Data;
            if (slot.Count == 0)
                return;

            if (whileBasesRise && (BlobchegRebase)slot.Code == BlobchegRebase.DomainNotRaised)
            {
                Clear();
                return;
            }

            Clear();

            var component = ComponentName(slot.TypeIndex);
            var domain = BlobchegDomainNames.Of(slot.DomainKey);
            var more = slot.Count > 1 ? $" (and {slot.Count - 1} more of the same)" : string.Empty;

            switch ((BlobchegRebase)slot.Code)
            {
                case BlobchegRebase.DomainNotRaised:
                    throw new InvalidOperationException(
                        $"Blobcheg: entities carrying '{component}' arrived before their base — domain " +
                        $"'{domain}' is not loaded, there is nothing to patch with{more}. Subscenes may only " +
                        "be loaded after the base-readiness singleton has been set");

                case BlobchegRebase.BadOffset:
                    throw new InvalidOperationException(
                        $"Blobcheg: '{component}' holds {slot.Value} — as an offset of domain '{domain}' " +
                        $"that is impossible{more}: it is either inside the header (the first " +
                        $"{BlobchegFormat.HeaderSize} B) or not a multiple of {BlobchegFormat.RecordAlign}. " +
                        "The start of a record does not look like that");

                case BlobchegRebase.OutOfRange:
                    throw new InvalidOperationException(
                        $"Blobcheg: '{component}' holds {slot.Value} — that is neither an offset of domain " +
                        $"'{domain}' nor an address of a live generation of its buffer{more}. It looks like the " +
                        "entity outlived a rebuild of the base whose buffer is already freed");

                case BlobchegRebase.WrongRecord:
                    throw new InvalidOperationException(
                        $"Blobcheg: a slot in '{component}' reached address {slot.Value} in domain " +
                        $"'{domain}', but no record of the declared type starts there{more}. Either the " +
                        "slot is typed with the wrong record, or a rebuild moved the layout and the generation " +
                        "translation handed out the neighbouring record — the entities need rebaking");

                default:
                    throw new InvalidOperationException(
                        $"Blobcheg: the patch of '{component}' failed with code {slot.Code}, value " +
                        $"{slot.Value}, domain '{domain}'{more}");
            }
        }

        // Registered types, not TypeManager: the error path must not risk a second failure.
        static string ComponentName(int typeIndex)
        {
            foreach (var type in BlobchegPatchTableBuilder.RegisteredTypes)
                if (type.TypeIndex.Value == typeIndex)
                    return type.GetManagedType()?.Name ?? $"type #{typeIndex}";

            return $"type #{typeIndex}";
        }
    }
}

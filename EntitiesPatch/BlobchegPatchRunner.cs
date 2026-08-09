using System;
using AOT;
using Unity.Burst;
using Unity.Entities;

namespace Blobcheg
{
    /// <summary>
    /// The patch itself. Called by the fork for every contiguous run of component elements: for an
    /// ordinary component once per type in a chunk, for a buffer once per entity.
    ///
    /// Burst code, so there are no exceptions here: a message with numbers substituted into it cannot
    /// be assembled under Burst. A failure is dropped into <see cref="BlobchegPatchErrors"/>, and the
    /// managed side shows it to a human — at the nearest update of the boot group.
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
                            // The check comes BEFORE the write: a failed patch is obliged to leave the
                            // slot as it was. Writing a foreign address and only then complaining would
                            // poison the field — and the next pass would translate the poison as a
                            // lawful generation address.
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
        /// Checks that a record of the expected type really does start at the resulting address. It
        /// leans on the debug contour of the file — the very one <c>BlobchegBlob.Read</c> checks the
        /// type against on the old path; a release player has no contour and no check either.
        ///
        /// Without it two troubles pass silently: a slot typed with a twin of the record, and a shifted
        /// layout after which the generation translation hands out the neighbouring record instead of
        /// its own.
        /// </summary>
        static bool RecordMatches(BlobchegFieldSlot* slot, ulong address)
        {
            if (slot->RecordTypeHash == 0)
                return true;

            if (!BlobchegBases.TryGetDebug(slot->DomainKey, out var basePtr, out var debugOffset))
                return true;

            // No contour — a release file, there is nothing to check against, and that is not a read error.
            if (debugOffset == 0)
                return true;

            var offset = (uint)(address - (ulong)basePtr);
            var entry = BlobchegDebugSection.Find(basePtr, debugOffset, offset);

            return entry != null && entry->TypeHash == slot->RecordTypeHash;
        }
    }

    /// <summary>
    /// The mailbox of patch failures: Burst code drops a code and numbers here, the managed side
    /// assembles a human message out of them. The first failure wins — the rest are only counted,
    /// otherwise a scene of ten thousand entities would bury the log under the same line.
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
        /// Throws on the first failure and clears the box. Called from the managed side, which is why
        /// the type name, the domain name and the repeat count are all here.
        ///
        /// <paramref name="whileBasesRise"/> is for those who ask while the bases are still loading:
        /// "the domain is not loaded" is not trouble for them but a state. On such a failure the slot
        /// stays an offset, untouched, and the very first pass after the base loads brings it to an
        /// address. The other codes throw here as well: a broken offset does not become whole just
        /// because the base was late.
        ///
        /// A second failure does not lie under the first in the box — only the count is kept — so along
        /// with a forgiven "the domain is not loaded" the count of whatever happened next is lost too.
        /// No loss: a broken slot is broken on the next pass as well, and there it will name itself.
        /// </summary>
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

        /// <summary>
        /// The type name is looked up in the list of registered ones rather than through the
        /// TypeManager: only the types the table itself put down get here, and reaching into the
        /// TypeManager from an error handler is one more way to fall on the road to the message.
        /// </summary>
        static string ComponentName(int typeIndex)
        {
            foreach (var type in BlobchegPatchTableBuilder.RegisteredTypes)
                if (type.TypeIndex.Value == typeIndex)
                    return type.GetManagedType()?.Name ?? $"type #{typeIndex}";

            return $"type #{typeIndex}";
        }
    }
}

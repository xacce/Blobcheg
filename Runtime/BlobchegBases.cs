using System;
using Unity.Burst;

namespace Blobcheg
{
    /// <summary>
    /// The outcome of translating a slot. A return code and not an exception: the translation is
    /// called from Burst code, where there is no exception with an assembled message, and different
    /// troubles are obliged to reach a human in different words.
    /// </summary>
    public enum BlobchegRebase : byte
    {
        /// <summary>The slot is empty or already in the needed form.</summary>
        Unchanged = 0,

        /// <summary>The slot was rewritten.</summary>
        Patched = 1,

        /// <summary>There is no base of this domain in the process — there is nothing to translate onto.</summary>
        DomainNotRaised = 2,

        /// <summary>The value is not an address of a live generation and as an offset does not fit into the base.</summary>
        OutOfRange = 3,

        /// <summary>As an offset the value is impossible: inside the header or not a multiple of the record alignment.</summary>
        BadOffset = 4,

        /// <summary>No record of the expected type starts at the resulting address.</summary>
        WrongRecord = 5,
    }

    /// <summary>
    /// The registry of the addresses of loaded bases: domain → where its buffer lies right now. It is
    /// needed by exactly those who work with a record by pointer rather than by offset — the entity
    /// patch and its checks.
    ///
    /// It lives in the runtime assembly and not next to the patch because <see cref="BlobchegBlob"/>
    /// registers itself here: a base can be loaded without Entities too, and the question "is the slot
    /// an address or still an offset" is asked by everyone alike.
    ///
    /// Retired generations of the buffer are kept next to the current one. Rebuilding a domain in the
    /// editor moves the base, and translating the addresses already handed out onto the new one is only
    /// possible while the old one is known; two imports in a row within one frame are enough to make a
    /// single previous generation too few, which is why there are
    /// <see cref="RetiredGenerations"/> of them. A retired generation is never dereferenced — only the
    /// arithmetic is taken from it — which is why a freed buffer stays in the list.
    ///
    /// Not a single managed static may appear in this class: Burst code reads it, and Burst drags the
    /// whole static constructor along. Domain names for messages live separately, in
    /// <see cref="BlobchegDomainNames"/>.
    /// </summary>
    public static unsafe class BlobchegBases
    {
        /// <summary>
        /// The ceiling of domains in a process. The registry is a flat array with a linear search:
        /// there are only a handful of domains in a project, and a hash map under Burst would cost more
        /// than the scan.
        /// </summary>
        public const int MaxDomains = 64;

        /// <summary>How many past generations of a domain buffer are remembered for translating pointers.</summary>
        public const int RetiredGenerations = 4;

        internal struct Table
        {
            public fixed ulong Keys[MaxDomains];
            public fixed ulong Ptrs[MaxDomains];
            public fixed int Lengths[MaxDomains];
            public fixed uint DebugOffsets[MaxDomains];
            public fixed ulong RetiredPtrs[MaxDomains * RetiredGenerations];
            public fixed int RetiredLengths[MaxDomains * RetiredGenerations];
            public int Count;
        }

        static readonly SharedStatic<Table> s_Table = SharedStatic<Table>.GetOrCreate<Table>();

        /// <summary>
        /// Puts the base of a domain on the register. Registering the same domain again means a rebuild:
        /// the previous address moves into the retired generations so that the pointers already handed
        /// out can be translated onto the new buffer.
        /// </summary>
        public static void Register(ulong domainKey, byte* ptr, int length, uint debugOffset = 0)
        {
            if (domainKey == 0)
                throw new ArgumentException("Blobcheg: a domain with a zero key", nameof(domainKey));

            if (ptr == null || length < BlobchegFormat.HeaderSize)
                throw new ArgumentException($"Blobcheg: the buffer of domain {domainKey:X16} is empty or shorter than the header");

            ref var t = ref s_Table.Data;

            var slot = IndexOf(ref t, domainKey);
            if (slot < 0)
                slot = AddSlot(ref t, domainKey);
            else
                Retire(ref t, slot);

            t.Ptrs[slot] = (ulong)ptr;
            t.Lengths[slot] = length;
            t.DebugOffsets[slot] = debugOffset;
        }

        /// <summary>
        /// Takes a particular buffer off the register, not the domain as a whole. The pointer argument
        /// is mandatory: on a rebuild the order can be anything, and the <c>Dispose</c> of the old base
        /// must not wipe out the live new one.
        ///
        /// The buffer taken off goes into the retired ones rather than being forgotten: entities already
        /// hold pointers into it, and the next load of the domain is obliged to be able to translate
        /// them.
        /// </summary>
        public static void Unregister(ulong domainKey, byte* ptr)
        {
            ref var t = ref s_Table.Data;

            var slot = IndexOf(ref t, domainKey);
            if (slot < 0)
                return;

            if (t.Ptrs[slot] != (ulong)ptr)
                return;

            Retire(ref t, slot);

            t.Ptrs[slot] = 0;
            t.Lengths[slot] = 0;
            t.DebugOffsets[slot] = 0;
        }

        /// <summary>The address and length of the current domain buffer. <c>false</c> means the base is not loaded.</summary>
        public static bool TryGet(ulong domainKey, out byte* ptr, out int length)
        {
            ref var t = ref s_Table.Data;

            var slot = IndexOf(ref t, domainKey);
            if (slot < 0 || t.Ptrs[slot] == 0)
            {
                ptr = null;
                length = 0;
                return false;
            }

            ptr = (byte*)t.Ptrs[slot];
            length = t.Lengths[slot];
            return true;
        }

        /// <summary>The debug contour of the current domain buffer. A zero <paramref name="debugOffset"/> means there is none.</summary>
        public static bool TryGetDebug(ulong domainKey, out byte* ptr, out uint debugOffset)
        {
            ref var t = ref s_Table.Data;

            var slot = IndexOf(ref t, domainKey);
            if (slot < 0 || t.Ptrs[slot] == 0)
            {
                ptr = null;
                debugOffset = 0;
                return false;
            }

            ptr = (byte*)t.Ptrs[slot];
            debugOffset = t.DebugOffsets[slot];
            return true;
        }

        /// <summary>
        /// Already an address inside the current buffer of this domain rather than an offset? The
        /// idempotence of the patch stands on this: an offset is measured from zero of the file and does
        /// not fall into the range of a real allocation.
        /// </summary>
        public static bool IsAddressOf(ulong domainKey, ulong value)
        {
            ref var t = ref s_Table.Data;

            var slot = IndexOf(ref t, domainKey);
            return slot >= 0 && InCurrent(ref t, slot, value);
        }

        /// <summary>
        /// An address inside the buffer of any loaded base. The question "is this a pointer at all" with
        /// no domain attached; the read checks use it, since the domain of a field is unknown to them.
        /// </summary>
        public static bool IsKnownAddress(ulong value)
        {
            ref var t = ref s_Table.Data;

            for (var i = 0; i < t.Count; i++)
                if (InCurrent(ref t, i, value))
                    return true;

            return false;
        }

        /// <summary>
        /// Turns the content of a slot into an address. Three entrances in one, because the caller does
        /// not tell them apart: the field may hold an offset (the entity has only just arrived), the
        /// address of the current generation (the patch has already happened) or the address of a
        /// retired one (the domain was rebuilt under a live world).
        ///
        /// It does not throw: it is called from Burst code. Dealing with the return code is the
        /// caller's business.
        /// </summary>
        public static BlobchegRebase TryResolve(ulong domainKey, ulong value, out ulong address)
        {
            address = value;

            if (value == 0)
                return BlobchegRebase.Unchanged;

            ref var t = ref s_Table.Data;

            var slot = IndexOf(ref t, domainKey);
            if (slot < 0 || t.Ptrs[slot] == 0)
                return BlobchegRebase.DomainNotRaised;

            var start = t.Ptrs[slot];

            // Already an address of the current generation — the patch is idempotent.
            if (InCurrent(ref t, slot, value))
                return BlobchegRebase.Unchanged;

            var retired = RetiredIndexOf(ref t, slot, value);
            if (retired >= 0)
            {
                // The address of a retired generation: the domain was rebuilt, the offset is the same,
                // the base moved.
                address = start + (value - t.RetiredPtrs[slot * RetiredGenerations + retired]);
                return BlobchegRebase.Patched;
            }

            // Past this point the value can only be an offset. There are no records inside the header,
            // and the start of a record is always aligned — otherwise it is not the address of a record,
            // whatever else it may be.
            if (value < BlobchegFormat.HeaderSize || (value & (BlobchegFormat.RecordAlign - 1)) != 0)
                return BlobchegRebase.BadOffset;

            if (value >= (ulong)t.Lengths[slot])
                return BlobchegRebase.OutOfRange;

            address = start + value;
            return BlobchegRebase.Patched;
        }

        /// <summary>
        /// The way back: an address into an offset again. Needed before writing a world — an offset is
        /// what must travel into the file, a process address is meaningless there. An offset on the
        /// input is left as it is.
        /// </summary>
        public static BlobchegRebase TryUnresolve(ulong domainKey, ulong value, out ulong offset)
        {
            offset = value;

            if (value == 0)
                return BlobchegRebase.Unchanged;

            ref var t = ref s_Table.Data;

            var slot = IndexOf(ref t, domainKey);

            // There is no domain at all — so there never was an address into it and the slot holds an
            // offset. A world that was never patched is saved as it is; this is the only place where a
            // domain that was not found is not an error.
            if (slot < 0)
                return BlobchegRebase.Unchanged;

            if (InCurrent(ref t, slot, value))
            {
                offset = value - t.Ptrs[slot];
                return BlobchegRebase.Patched;
            }

            var retired = RetiredIndexOf(ref t, slot, value);
            if (retired >= 0)
            {
                offset = value - t.RetiredPtrs[slot * RetiredGenerations + retired];
                return BlobchegRebase.Patched;
            }

            // It fell into no generation — so this is not a pointer but a number that is already an
            // offset. Whether it is a good one the way back does not judge: the forward pass already
            // rejected it out loud, and a second refusal over the same number would make a world with
            // one broken reference unsaveable. The strictness of the two directions of one pass is
            // obliged to be the same.
            return BlobchegRebase.Unchanged;
        }

        /// <summary>Tests only: take everything off and start with a clean registry.</summary>
        internal static void Clear()
        {
            ref var t = ref s_Table.Data;

            for (var i = 0; i < MaxDomains; i++)
            {
                t.Keys[i] = 0;
                t.Ptrs[i] = 0;
                t.Lengths[i] = 0;
                t.DebugOffsets[i] = 0;

                for (var g = 0; g < RetiredGenerations; g++)
                {
                    t.RetiredPtrs[i * RetiredGenerations + g] = 0;
                    t.RetiredLengths[i * RetiredGenerations + g] = 0;
                }
            }

            t.Count = 0;
        }

        static int AddSlot(ref Table t, ulong domainKey)
        {
            if (t.Count == MaxDomains)
                throw new InvalidOperationException(
                    $"Blobcheg: more than {MaxDomains} domains in the process — the ceiling of the address registry");

            var slot = t.Count++;
            t.Keys[slot] = domainKey;
            t.Ptrs[slot] = 0;
            t.Lengths[slot] = 0;
            t.DebugOffsets[slot] = 0;

            for (var g = 0; g < RetiredGenerations; g++)
            {
                t.RetiredPtrs[slot * RetiredGenerations + g] = 0;
                t.RetiredLengths[slot * RetiredGenerations + g] = 0;
            }

            return slot;
        }

        /// <summary>Shifts the current generation to the head of the retired list; the oldest one falls out.</summary>
        static void Retire(ref Table t, int slot)
        {
            if (t.Ptrs[slot] == 0)
                return;

            var b = slot * RetiredGenerations;
            for (var g = RetiredGenerations - 1; g > 0; g--)
            {
                t.RetiredPtrs[b + g] = t.RetiredPtrs[b + g - 1];
                t.RetiredLengths[b + g] = t.RetiredLengths[b + g - 1];
            }

            t.RetiredPtrs[b] = t.Ptrs[slot];
            t.RetiredLengths[b] = t.Lengths[slot];
        }

        static bool InCurrent(ref Table t, int slot, ulong value)
        {
            var start = t.Ptrs[slot];
            return start != 0 && value >= start && value < start + (ulong)t.Lengths[slot];
        }

        static int RetiredIndexOf(ref Table t, int slot, ulong value)
        {
            var b = slot * RetiredGenerations;
            for (var g = 0; g < RetiredGenerations; g++)
            {
                var start = t.RetiredPtrs[b + g];
                if (start != 0 && value >= start && value < start + (ulong)t.RetiredLengths[b + g])
                    return g;
            }

            return -1;
        }

        static int IndexOf(ref Table t, ulong domainKey)
        {
            for (var i = 0; i < t.Count; i++)
                if (t.Keys[i] == domainKey)
                    return i;

            return -1;
        }
    }
}

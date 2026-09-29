using System;
using Unity.Burst;

namespace Blobcheg
{
    public enum BlobchegRebase : byte // a return code, not an exception: resolved from Burst code
    {
        Unchanged = 0, // empty or already in the needed form

        Patched = 1,

        DomainNotRaised = 2, // no base of this domain in the process

        OutOfRange = 3, // not a live-generation address, and as an offset past the base

        BadOffset = 4, // inside the header or not aligned to a record

        WrongRecord = 5, // no record of the expected type at the resulting address
    }

    // Domain -> current buffer address, plus retired generations to translate handed-out pointers.
    public static unsafe class BlobchegBases
    {
        public const int MaxDomains = 64; // flat array with a linear scan: cheaper than a hash map under Burst

        public const int RetiredGenerations = 4; // two imports in one frame outlive a single previous one

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

        static readonly SharedStatic<Table> s_Table = SharedStatic<Table>.GetOrCreate<Table>(); // no managed statics: Burst drags the cctor

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
                Retire(ref t, slot); // re-registering is a rebuild: keep the old address for translation

            t.Ptrs[slot] = (ulong)ptr;
            t.Lengths[slot] = length;
            t.DebugOffsets[slot] = debugOffset;
        }

        public static void Unregister(ulong domainKey, byte* ptr)
        {
            ref var t = ref s_Table.Data;

            var slot = IndexOf(ref t, domainKey);
            if (slot < 0)
                return;

            if (t.Ptrs[slot] != (ulong)ptr) // the old base's Dispose must not wipe the live new one
                return;

            Retire(ref t, slot); // entities still hold pointers into it

            t.Ptrs[slot] = 0;
            t.Lengths[slot] = 0;
            t.DebugOffsets[slot] = 0;
        }

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
            debugOffset = t.DebugOffsets[slot]; // zero means no debug contour
            return true;
        }

        public static int SlotCount => s_Table.Data.Count; // slots are never reclaimed: check each one

        public static bool TryGetSlot(int index, out ulong domainKey, out byte* ptr, out int length,
            out uint debugOffset) // out-params: a struct would drag its cctor into Burst
        {
            ref var t = ref s_Table.Data;

            if ((uint)index >= (uint)t.Count || t.Ptrs[index] == 0)
            {
                domainKey = 0;
                ptr = null;
                length = 0;
                debugOffset = 0;
                return false;
            }

            domainKey = t.Keys[index];
            ptr = (byte*)t.Ptrs[index];
            length = t.Lengths[index];
            debugOffset = t.DebugOffsets[index];
            return true;
        }

        public static bool TryGetRetired(int index, int generation, out ulong address, out int length)
        {
            ref var t = ref s_Table.Data;

            if ((uint)index >= (uint)t.Count || (uint)generation >= RetiredGenerations)
            {
                address = 0;
                length = 0;
                return false;
            }

            var at = index * RetiredGenerations + generation;
            address = t.RetiredPtrs[at]; // a number, not a pointer: a retired generation is never dereferenced
            length = t.RetiredLengths[at];
            return address != 0;
        }

        public static bool Has(ulong domainKey)
        {
            ref var t = ref s_Table.Data;

            var slot = IndexOf(ref t, domainKey);
            return slot >= 0 && t.Ptrs[slot] != 0;
        }

        public static bool IsAddressOf(ulong domainKey, ulong value)
        {
            ref var t = ref s_Table.Data;

            var slot = IndexOf(ref t, domainKey);
            return slot >= 0 && InCurrent(ref t, slot, value); // offsets never hit a real allocation: patch idempotence
        }

        public static bool IsKnownAddress(ulong value)
        {
            ref var t = ref s_Table.Data;

            for (var i = 0; i < t.Count; i++)
                if (InCurrent(ref t, i, value))
                    return true;

            return false;
        }

        // A slot holds an offset, a current address or a retired one; never throws, called from Burst.
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

            if (InCurrent(ref t, slot, value))
                return BlobchegRebase.Unchanged;

            var retired = RetiredIndexOf(ref t, slot, value);
            if (retired >= 0) // domain rebuilt: same offset, moved base
            {
                address = start + (value - t.RetiredPtrs[slot * RetiredGenerations + retired]);
                return BlobchegRebase.Patched;
            }

            if (value < BlobchegFormat.HeaderSize || (value & (BlobchegFormat.RecordAlign - 1)) != 0)
                return BlobchegRebase.BadOffset; // records never sit inside the header and are always aligned

            if (value >= (ulong)t.Lengths[slot])
                return BlobchegRebase.OutOfRange;

            address = start + value;
            return BlobchegRebase.Patched;
        }

        // Address back to an offset before a world is written; an offset passes through.
        public static BlobchegRebase TryUnresolve(ulong domainKey, ulong value, out ulong offset)
        {
            offset = value;

            if (value == 0)
                return BlobchegRebase.Unchanged;

            ref var t = ref s_Table.Data;

            var slot = IndexOf(ref t, domainKey);

            if (slot < 0) // never patched, saved as is: the only place a missing domain is not an error
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

            return BlobchegRebase.Unchanged; // already an offset; re-rejecting it would make the world unsaveable
        }

        internal static void Clear() // tests only
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

        static void Retire(ref Table t, int slot) // current generation to the head of the retired list, oldest drops
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

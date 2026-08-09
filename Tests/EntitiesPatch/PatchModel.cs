using System.Runtime.InteropServices;
using Unity.Entities;

namespace Blobcheg.PatchTests
{
    // ------------------------------------------------------------------ domains
    //
    // Three domains. The hot and the cold one exist because half the set is about "and what if the
    // offset comes from a FOREIGN base"; with one domain that question cannot be asked. The third, the
    // ghost one, is declared by a base but is never loaded in the tests: the promise "the domain is not
    // loaded means an explicit error" is checked on it.
    //
    // A domain is obliged to be declared through [Blobcheg] — otherwise BlobchegPatchTableBuilder never
    // learns about it and fails the table build on the very first reference into it.

    /// <summary>The hot base. Loaded in almost every test.</summary>
    public interface IPatchHot
    {
    }

    /// <summary>The cold base. The source of FOREIGN offsets.</summary>
    public interface IPatchCold
    {
    }

    /// <summary>A domain whose base is never loaded.</summary>
    public interface IPatchGhost
    {
    }

    [Blobcheg(typeof(IPatchHot))]
    public partial struct PatchHotDb
    {
    }

    [Blobcheg(typeof(IPatchCold))]
    public partial struct PatchColdDb
    {
    }

    [Blobcheg(typeof(IPatchGhost))]
    public partial struct PatchGhostDb
    {
    }

    // ------------------------------------------------------------------ records

    public struct PatchGun : IPatchHot
    {
        public float Ammo;
        public int Rpm;
    }

    /// <summary>
    /// A twin of the gun: the same size, a different type. It is used to check whether the new path
    /// catches reading a record with a foreign type the way the old one does (<c>Read&lt;T&gt;</c>
    /// through the debug contour).
    /// </summary>
    public struct PatchArmor : IPatchHot
    {
        public float Hp;
        public int Plates;
    }

    public struct PatchNote : IPatchCold
    {
        public int Tier;
        public int Extra;
    }

    public struct PatchGhostRecord : IPatchGhost
    {
        public int V;
    }

    /// <summary>
    /// A record that belongs to no domain. There is NO component with a reference to it in the assembly
    /// and there cannot be: the build of the patch table fails on such a reference entirely, that is, it
    /// would switch the patch off for the whole project. It is checked by reflection — see
    /// <c>DomainTests</c>.
    /// </summary>
    public struct PatchLoose
    {
        public int V;
    }

    /// <summary>A record in two domains at once. There is no component referencing it, for the same reason.</summary>
    public struct PatchBoth : IPatchHot, IPatchCold
    {
        public int V;
    }

    /// <summary>
    /// A record with a slot INSIDE it. Absurd by construction: a reference to a reference. It exists to
    /// check that the patch walks the memory of components and does not climb inside the base itself.
    /// </summary>
    public struct PatchRefRecord : IPatchHot
    {
        public BlobchegReference<PatchGun> Inner;
        public long Tag;
    }

    // ------------------------------------------------------------------ components

    public struct GunRef : IComponentData
    {
        public BlobchegReference<PatchGun> Gun;
    }

    /// <summary>A second component type with the same slot — for "one offset in two components".</summary>
    public struct GunRefTwin : IComponentData
    {
        public BlobchegReference<PatchGun> Gun;
    }

    public struct ArmorRef : IComponentData
    {
        public BlobchegReference<PatchArmor> Armor;
    }

    public struct NoteRef : IComponentData
    {
        public BlobchegReference<PatchNote> Note;
    }

    /// <summary>A reference into a domain whose base is not loaded.</summary>
    public struct GhostRef : IComponentData
    {
        public BlobchegReference<PatchGhostRecord> Ghost;
    }

    /// <summary>Two slots of different record types in one component — they must not be mixed up.</summary>
    public struct PairRef : IComponentData
    {
        public BlobchegReference<PatchGun> Gun;
        public BlobchegReference<PatchArmor> Armor;
    }

    /// <summary>A component without a single slot: the patch is obliged not to notice it.</summary>
    public struct PlainData : IComponentData
    {
        public int Value;
    }

    /// <summary>
    /// A slot as the second field after an unaligned byte. With <c>Pack = 1</c> it lies at byte offset
    /// 1, that is, the walk is obliged to return 1 and the patch to write eight bytes at an unaligned
    /// address. The trailing byte is there so that it is visible if the patch climbed past the edge of
    /// the slot.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct PackedRef : IComponentData
    {
        public byte Head;
        public BlobchegReference<PatchGun> Gun;
        public byte Tail;
    }

    public struct NestOne
    {
        public int A;
        public BlobchegReference<PatchGun> Gun;
    }

    public struct NestTwo
    {
        public short S;
        public NestOne Inner;
    }

    /// <summary>A slot at the second level of nesting.</summary>
    public struct ShallowNestRef : IComponentData
    {
        public int Head;
        public NestOne Inner;
    }

    /// <summary>A slot at the third level of nesting.</summary>
    public struct DeepNestRef : IComponentData
    {
        public long Head;
        public NestTwo Inner;
    }

    /// <summary>A buffer element with a slot — patched element by element.</summary>
    public struct RefElement : IBufferElementData
    {
        public BlobchegReference<PatchGun> Gun;
        public int Marker;
    }

    /// <summary>A reference to a record that itself consists of a reference.</summary>
    public struct RecordRef : IComponentData
    {
        public BlobchegReference<PatchRefRecord> Record;
    }

    /// <summary>
    /// The same slot, but in a SHARED component. <c>ISharedComponentData</c> does not inherit
    /// <c>IComponentData</c>, so the type walk does not see it — the question is whether the developer
    /// finds out about that in any way at all.
    /// </summary>
    public struct SharedRef : ISharedComponentData
    {
        public BlobchegReference<PatchGun> Gun;
    }
}

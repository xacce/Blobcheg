using System.Runtime.InteropServices;
using Unity.Entities;

namespace Blobcheg.PatchTests
{
    // Hot + cold test offsets from a FOREIGN base; the ghost base is never loaded (explicit-error check).
    public interface IPatchHot
    {
    }

    public interface IPatchCold
    {
    }

    public interface IPatchGhost
    {
    }

    [Blobcheg(typeof(IPatchHot))] // an undeclared domain fails the patch table build on first reference
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

    public struct PatchGun : IPatchHot
    {
        public float Ammo;
        public int Rpm;
    }

    // Gun twin, same size, other type: the new path must catch a foreign-type Read<T> like the old one.
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

    public struct PatchLoose // no domain: a component ref would break the whole patch table; see DomainTests
    {
        public int V;
    }

    public struct PatchBoth : IPatchHot, IPatchCold // unreferenced for the same reason
    {
        public int V;
    }

    public struct PatchRefRecord : IPatchHot // ref-to-ref: the patch must not climb inside the base
    {
        public BlobchegReference<PatchGun> Inner;
        public long Tag;
    }

    public struct GunRef : IComponentData
    {
        public BlobchegReference<PatchGun> Gun;
    }

    public struct GunRefTwin : IComponentData // one offset in two components
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

    public struct GhostRef : IComponentData
    {
        public BlobchegReference<PatchGhostRecord> Ghost;
    }

    public struct PairRef : IComponentData // two slot types that must not be mixed up
    {
        public BlobchegReference<PatchGun> Gun;
        public BlobchegReference<PatchArmor> Armor;
    }

    public struct PlainData : IComponentData // no slot: the patch must ignore it
    {
        public int Value;
    }

    // Pack = 1 puts the slot at byte 1 (unaligned write); Tail exposes a patch overrunning the slot.
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

    public struct ShallowNestRef : IComponentData
    {
        public int Head;
        public NestOne Inner;
    }

    public struct DeepNestRef : IComponentData
    {
        public long Head;
        public NestTwo Inner;
    }

    public struct RefElement : IBufferElementData // patched element by element
    {
        public BlobchegReference<PatchGun> Gun;
        public int Marker;
    }

    public struct RecordRef : IComponentData
    {
        public BlobchegReference<PatchRefRecord> Record;
    }

    // Shared components are not IComponentData, so the walk misses them: does the developer find out?
    public struct SharedRef : ISharedComponentData
    {
        public BlobchegReference<PatchGun> Gun;
    }
}

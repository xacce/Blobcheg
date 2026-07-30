using System.Runtime.InteropServices;
using Unity.Entities;

namespace Blobcheg.PatchTests
{
    // ------------------------------------------------------------------ домены
    //
    // Три домена. Горячий и холодный нужны затем, что половина набора — про «а если оффсет из
    // ЧУЖОЙ базы»; с одним доменом этот вопрос не задать. Третий, призрачный, объявлен базой, но в
    // тестах не поднимается никогда: на нём проверяется обещание «домен не поднят — явная ошибка».
    //
    // Домен обязан быть объявлен через [Blobcheg] — иначе BlobchegPatchTableBuilder про него не
    // узнает и уронит сборку таблицы на первой же ссылке в него.

    /// <summary>Горячая база. Поднимается почти в каждом тесте.</summary>
    public interface IPatchHot
    {
    }

    /// <summary>Холодная база. Источник ЧУЖИХ оффсетов.</summary>
    public interface IPatchCold
    {
    }

    /// <summary>Домен, база которого не поднимается никогда.</summary>
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

    // ------------------------------------------------------------------ записи

    public struct PatchGun : IPatchHot
    {
        public float Ammo;
        public int Rpm;
    }

    /// <summary>
    /// Близнец пушки: тот же размер, другой тип. На нём проверяется, ловит ли новый путь чтение
    /// записи чужим типом так же, как ловит старый (<c>Read&lt;T&gt;</c> через отладочный контур).
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
    /// Запись, не входящая ни в один домен. Компонента со ссылкой на неё в сборке НЕТ и быть не
    /// может: сборка таблицы патча падает на такой ссылке целиком, то есть выключила бы патч всему
    /// проекту. Проверяется рефлексией — см. <c>DomainTests</c>.
    /// </summary>
    public struct PatchLoose
    {
        public int V;
    }

    /// <summary>Запись сразу в двух доменах. Компоненты со ссылкой на неё — по той же причине нет.</summary>
    public struct PatchBoth : IPatchHot, IPatchCold
    {
        public int V;
    }

    /// <summary>
    /// Запись, ВНУТРИ которой лежит слот. Абсурд по постановке: ссылка на ссылку. Существует
    /// затем, чтобы проверить, что патч ходит по памяти компонентов и не лезет внутрь самой базы.
    /// </summary>
    public struct PatchRefRecord : IPatchHot
    {
        public BlobchegReference<PatchGun> Inner;
        public long Tag;
    }

    // ------------------------------------------------------------------ компоненты

    public struct GunRef : IComponentData
    {
        public BlobchegReference<PatchGun> Gun;
    }

    /// <summary>Второй тип компонента с тем же слотом — под «один оффсет в двух компонентах».</summary>
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

    /// <summary>Ссылка в домен, база которого не поднимается.</summary>
    public struct GhostRef : IComponentData
    {
        public BlobchegReference<PatchGhostRecord> Ghost;
    }

    /// <summary>Два слота разных типов записи в одном компоненте — перепутать их нельзя.</summary>
    public struct PairRef : IComponentData
    {
        public BlobchegReference<PatchGun> Gun;
        public BlobchegReference<PatchArmor> Armor;
    }

    /// <summary>Компонент без единого слота: патч обязан его не заметить.</summary>
    public struct PlainData : IComponentData
    {
        public int Value;
    }

    /// <summary>
    /// Слот вторым полем после невыровненного байта. При <c>Pack = 1</c> он лежит по байтовому
    /// оффсету 1, то есть обход обязан отдать 1, а патч — записать восемь байт по невыровненному
    /// адресу. Хвостовой байт стоит затем, чтобы было видно, если патч залез за край слота.
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

    /// <summary>Слот на второй ступени вложенности.</summary>
    public struct ShallowNestRef : IComponentData
    {
        public int Head;
        public NestOne Inner;
    }

    /// <summary>Слот на третьей ступени вложенности.</summary>
    public struct DeepNestRef : IComponentData
    {
        public long Head;
        public NestTwo Inner;
    }

    /// <summary>Элемент буфера со слотом — патчится поэлементно.</summary>
    public struct RefElement : IBufferElementData
    {
        public BlobchegReference<PatchGun> Gun;
        public int Marker;
    }

    /// <summary>Ссылка на запись, которая сама состоит из ссылки.</summary>
    public struct RecordRef : IComponentData
    {
        public BlobchegReference<PatchRefRecord> Record;
    }

    /// <summary>
    /// Тот же слот, но в ОБЩЕМ компоненте. <c>ISharedComponentData</c> не наследует
    /// <c>IComponentData</c>, поэтому обход типов его не видит — вопрос в том, узнает ли об этом
    /// разработчик хоть как-нибудь.
    /// </summary>
    public struct SharedRef : ISharedComponentData
    {
        public BlobchegReference<PatchGun> Gun;
    }
}

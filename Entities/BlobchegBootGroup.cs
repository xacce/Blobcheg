using Unity.Entities;

namespace Blobcheg
{
    /// <summary>
    /// Группа подъёма баз и роутеров. Стоит в самом начале инициализации — **до**
    /// <see cref="BeginInitializationEntityCommandBufferSystem"/>: командный буфер проигрывает
    /// структурные изменения кадра, и системы, которым база уже нужна, обязаны увидеть её раньше,
    /// чем свои сущности.
    ///
    /// Сюда кодоген кладёт бут-систему на каждую базу и роутер, объявленные <c>IComponentData</c>.
    /// Своя система подъёма при этом не запрещена: положи её в эту же группу.
    ///
    /// Группа заведена и в редакторном мире — иначе кодогенная бут-система, которая там живёт,
    /// осталась бы без своей группы. Наследование при этом не тронуто: рукописная система в этой
    /// группе по-прежнему попадает только в игровой мир, а чтобы поднимать базу и в редакторном,
    /// ей надо сказать это самой — <c>[WorldSystemFilter(Default | Editor)]</c>.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.Default | WorldSystemFilterFlags.Editor)]
    [UpdateInGroup(typeof(InitializationSystemGroup), OrderFirst = true)]
    [UpdateBefore(typeof(BeginInitializationEntityCommandBufferSystem))]
    public partial class BlobchegBootGroup : ComponentSystemGroup
    {
    }
}

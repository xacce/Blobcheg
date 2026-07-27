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
    /// </summary>
    [UpdateInGroup(typeof(InitializationSystemGroup), OrderFirst = true)]
    [UpdateBefore(typeof(BeginInitializationEntityCommandBufferSystem))]
    public partial class BlobchegBootGroup : ComponentSystemGroup
    {
    }
}

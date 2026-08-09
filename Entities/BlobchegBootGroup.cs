using Unity.Entities;

namespace Blobcheg
{
    /// <summary>
    /// The group that loads bases and routers. It stands at the very start of initialisation — **before**
    /// <see cref="BeginInitializationEntityCommandBufferSystem"/>: the command buffer plays back the
    /// structural changes of the frame, and systems that already need a base are obliged to see it
    /// earlier than their own entities.
    ///
    /// This is where the codegen puts a boot system for every base and router declared as
    /// <c>IComponentData</c>. A hand-written load system is not forbidden either: put it into the same
    /// group.
    ///
    /// The group exists in the editor world too — otherwise the generated boot system that lives there
    /// would be left without its group. Inheritance is untouched by that: a hand-written system in this
    /// group still ends up in the game world only, and to load a base in the editor world as well it
    /// has to say so itself — <c>[WorldSystemFilter(Default | Editor)]</c>.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.Default | WorldSystemFilterFlags.Editor)]
    [UpdateInGroup(typeof(InitializationSystemGroup), OrderFirst = true)]
    [UpdateBefore(typeof(BeginInitializationEntityCommandBufferSystem))]
    public partial class BlobchegBootGroup : ComponentSystemGroup
    {
    }
}

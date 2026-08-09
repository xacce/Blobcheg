using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using UnityEngine;

namespace Blobcheg
{
    /// <summary>
    /// Installs the patch into the fork: it builds the slot table and hands the fork two entry points —
    /// a Burst function for running over elements and a managed handler for the live path.
    ///
    /// Building the table requires an initialised TypeManager, so <see cref="TypeManager.Initialize"/>
    /// is called explicitly: it is idempotent, and the order of the domain initialisers gives no
    /// guarantees.
    /// </summary>
    public static unsafe class BlobchegPatchInstall
    {
        static bool s_Installed;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterAssembliesLoaded)]
        public static void Install()
        {
            if (s_Installed)
                return;

            TypeManager.Initialize();
            BlobchegPatchTableBuilder.Build();

            BlobchegPatchHook.PatchElementsHook =
                BurstCompiler.CompileFunctionPointer<BlobchegPatchHook.PatchElements>(BlobchegPatchRunner.PatchElements);
            BlobchegPatchHook.AfterApplyChangeSet = BlobchegLiveSweep.Run;
            BlobchegPatchHook.AfterSerializeWorld = () => BlobchegPatchErrors.ThrowIfAny();

            // The same pass is handed outwards: everyone who loaded a base uses it — the generated boot
            // system and a hand-written load alike.
            BlobchegSweep.Hook = BlobchegLiveSweep.Run;

            // The diagnostics of building the table do NOT go into the log, and that is not
            // forgetfulness. The walk sees every type in the process, including the package's own test
            // fixtures that are declared wrongly on purpose — so the consumer's console would get an
            // error about someone else's test right after installation. The real signal exists anyway
            // and is more precise: a slot left as an offset throws on the first Value, at the place and
            // with the type name. The list stays in BlobchegPatchTableBuilder.Diagnostics for tools and
            // tests.
            s_Installed = true;
        }

#if UNITY_EDITOR
        [UnityEditor.InitializeOnLoadMethod]
        static void InstallInEditor()
        {
            Install();

            // The table holds persistent memory, and a domain reload wipes the managed side but not the
            // native one. Without uninstalling, every recompilation would leave a leak behind.
            UnityEditor.AssemblyReloadEvents.beforeAssemblyReload += Uninstall;
        }
#endif

        internal static void Uninstall()
        {
            BlobchegPatchHook.PatchElementsHook = default;
            BlobchegPatchHook.AfterApplyChangeSet = null;
            BlobchegPatchHook.AfterSerializeWorld = null;
            BlobchegSweep.Hook = null;
            BlobchegPatchErrors.Clear();
            BlobchegPatchTableBuilder.Destroy();
            s_Installed = false;
        }
    }

    /// <summary>
    /// The live path. An open subscene does not go through deserialisation: the baking world is diffed
    /// against the shadow one and the result is applied to the game world as a change set — and after
    /// that the slots hold offsets.
    ///
    /// There is no need to take the change set apart: the patch is idempotent by its range check, so
    /// walking every entity carrying our components is cheaper than finding out which ones the apply
    /// actually rewrote.
    ///
    /// The load of a base uses the same pass: it both translates the slots that arrived before their
    /// base and moves them from the previous domain buffer onto the new one after a rebuild.
    /// </summary>
    public static unsafe class BlobchegLiveSweep
    {
        public static void Run(EntityManager entityManager)
        {
            if (!BlobchegPatchTable.IsBuilt)
                return;

            foreach (var componentType in BlobchegPatchTableBuilder.RegisteredTypes)
                Sweep(entityManager, componentType);

            // "The domain is not loaded" is no trouble here: this pass lives where authoring happens,
            // and the order in which bases load in the editor world does not obey it. The slot stayed an
            // offset, and the pass right after the base loads will bring it to an address.
            BlobchegPatchErrors.ThrowIfAny(whileBasesRise: true);
        }

        static void Sweep(EntityManager entityManager, ComponentType componentType)
        {
            var types = new NativeList<ComponentType>(1, Allocator.Temp) { componentType };

            var query = new EntityQueryBuilder(Allocator.Temp)
                .WithAll(ref types)
                .WithOptions(EntityQueryOptions.IncludeDisabledEntities | EntityQueryOptions.IncludePrefab)
                .Build(entityManager);

            if (query.IsEmpty)
            {
                query.Dispose();
                types.Dispose();
                return;
            }

            var handle = entityManager.GetDynamicComponentTypeHandle(componentType);
            var typeIndex = componentType.TypeIndex.Value;
            var chunks = query.ToArchetypeChunkArray(Allocator.Temp);

            foreach (var chunk in chunks)
            {
                if (componentType.IsBuffer)
                {
                    var accessor = chunk.GetUntypedBufferAccessor(ref handle);
                    var elementSize = accessor.ElementSize;

                    for (var i = 0; i < accessor.Length; i++)
                    {
                        var elements = (byte*)accessor.GetUnsafePtrAndLength(i, out var length);
                        BlobchegPatchRunner.PatchElements(
                            typeIndex, elements, length, elementSize, BlobchegPatchRunner.ModeResolve);
                    }
                }
                else
                {
                    var size = TypeManager.GetTypeInfo(componentType.TypeIndex).TypeSize;
                    var array = chunk.GetDynamicComponentDataArrayReinterpret<byte>(ref handle, size);

                    BlobchegPatchRunner.PatchElements(
                        typeIndex, (byte*)NativeArrayUnsafeUtility.GetUnsafePtr(array), chunk.Count, size,
                        BlobchegPatchRunner.ModeResolve);
                }
            }

            chunks.Dispose();
            query.Dispose();
            types.Dispose();
        }
    }

    /// <summary>
    /// Shows the failures of the patch to a human. The patch itself lives in Burst code and drops them
    /// into a box silently; without this system "the entities arrived before the base" would look like
    /// zeroes in fields.
    ///
    /// It stands in the boot group, that is, at the very start of initialisation — a frame later than
    /// the streaming of the section, but with a full message.
    ///
    /// The system is needed in the editor world too (otherwise failures would pile up there silently),
    /// but there it forgives "the domain is not loaded": in the editor world subscenes are loaded by
    /// Unity whenever it finds convenient, while bases are loaded by reading a file, and one overtaking
    /// the other is lawful here. In the player the order is ours, and an entity that arrived before the
    /// base stays an error.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.Default | WorldSystemFilterFlags.Editor)]
    [UpdateInGroup(typeof(BlobchegBootGroup))]
    public partial struct BlobchegPatchErrorSystem : ISystem
    {
        public void OnUpdate(ref SystemState state)
        {
            if (!BlobchegPatchErrors.HasAny)
                return;

#if UNITY_EDITOR
            BlobchegPatchErrors.ThrowIfAny(whileBasesRise: true);
#else
            BlobchegPatchErrors.ThrowIfAny();
#endif
        }
    }
}

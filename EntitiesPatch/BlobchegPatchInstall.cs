using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using UnityEngine;

namespace Blobcheg
{
    public static unsafe class BlobchegPatchInstall
    {
        static bool s_Installed;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterAssembliesLoaded)]
        public static void Install()
        {
            if (s_Installed)
                return;

            TypeManager.Initialize(); // idempotent; the domain initialiser order is not guaranteed
            BlobchegPatchTableBuilder.Build();

            BlobchegPatchHook.PatchElementsHook =
                BurstCompiler.CompileFunctionPointer<BlobchegPatchHook.PatchElements>(BlobchegPatchRunner.PatchElements);
            BlobchegPatchHook.AfterApplyChangeSet = BlobchegLiveSweep.Run;
            BlobchegPatchHook.AfterSerializeWorld = () => BlobchegPatchErrors.ThrowIfAny();

            BlobchegSweep.Hook = BlobchegLiveSweep.Run; // shared with every base loader, generated or not

            s_Installed = true; // table diagnostics stay out of the log: package test fixtures are wrong on purpose
        }

#if UNITY_EDITOR
        [UnityEditor.InitializeOnLoadMethod]
        static void InstallInEditor()
        {
            Install();

            // A domain reload keeps the table's native memory; without uninstalling every recompile leaks.
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

    public static unsafe class BlobchegLiveSweep // change sets leave offsets; the patch is idempotent, sweep all
    {
        public static void Run(EntityManager entityManager)
        {
            if (!BlobchegPatchTable.IsBuilt)
                return;

            // Runs on every apply of a change set: the price grows with the world, not with the edit.
            using var work = BlobchegProfile.Begin("a patch pass over the world");
            var touched = 0;

            foreach (var componentType in BlobchegPatchTableBuilder.RegisteredTypes)
                touched += Sweep(entityManager, componentType);

            work.Note($"{BlobchegPatchTableBuilder.RegisteredTypes.Count} types, {touched} entities");

            BlobchegPatchErrors.ThrowIfAny(whileBasesRise: true); // editor base load order is not ours: an unloaded domain is resolved by the pass after the load.
        }

        static int Sweep(EntityManager entityManager, ComponentType componentType)
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
                return 0;
            }

            var handle = entityManager.GetDynamicComponentTypeHandle(componentType);
            var typeIndex = componentType.TypeIndex.Value;
            var chunks = query.ToArchetypeChunkArray(Allocator.Temp);
            var touched = 0;

            foreach (var chunk in chunks)
            {
                touched += chunk.Count;

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
            return touched;
        }
    }

    // Surfaces the Burst patch's silent failures; the editor world forgives entities that beat their base.
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

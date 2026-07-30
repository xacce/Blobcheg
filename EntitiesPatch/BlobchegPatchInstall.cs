using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using UnityEngine;

namespace Blobcheg
{
    /// <summary>
    /// Ставит патч в форк: собирает таблицу слотов и отдаёт форку две точки входа — Burst-функцию на
    /// прогон элементов и managed-обработчик живого пути.
    ///
    /// Сборка таблицы требует поднятого TypeManager, поэтому <see cref="TypeManager.Initialize"/>
    /// зовётся явно: он идемпотентен, а порядок инициализаторов домена гарантий не даёт.
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

            // Тот же проход отдаётся наружу: им пользуется всякий, кто поднял базу — кодогенная
            // бут-система и рукописный подъём одинаково.
            BlobchegSweep.Hook = BlobchegLiveSweep.Run;

            // Диагностика сборки таблицы в лог НЕ уходит, и это не забывчивость. Обход видит все
            // типы процесса, включая тестовые фикстуры пакета, объявленные неправильно нарочно, —
            // так консоль потребителя получала бы error про чужой тест сразу после установки.
            // Настоящий сигнал и так есть и точнее: слот, оставшийся оффсетом, бросает на первом
            // Value, по месту и с именем типа. Список остаётся в BlobchegPatchTableBuilder.Diagnostics
            // для инструментов и тестов.
            s_Installed = true;
        }

#if UNITY_EDITOR
        [UnityEditor.InitializeOnLoadMethod]
        static void InstallInEditor()
        {
            Install();

            // Таблица держит persistent-память, а перезагрузка домена стирает managed-сторону, но не
            // нативную. Без снятия каждая перекомпиляция оставляла бы за собой утечку.
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
    /// Живой путь. Открытая сабсцена не ходит через десериализацию: бейкинг-мир диффится с теневым,
    /// результат накатывается в игровой мир чейнджсетом — и в слотах после этого лежат оффсеты.
    ///
    /// Разбирать чейнджсет не нужно: патч идемпотентен по диапазонной проверке, поэтому дешевле
    /// пройти все сущности с нашими компонентами, чем выяснять, какие именно переписал апплай.
    ///
    /// Этим же проходом пользуется подъём базы: он и переводит слоты, приехавшие раньше своей базы,
    /// и переселяет их с прежнего буфера домена на новый после пересборки.
    /// </summary>
    public static unsafe class BlobchegLiveSweep
    {
        public static void Run(EntityManager entityManager)
        {
            if (!BlobchegPatchTable.IsBuilt)
                return;

            foreach (var componentType in BlobchegPatchTableBuilder.RegisteredTypes)
                Sweep(entityManager, componentType);

            // «Домен не поднят» здесь не беда: этот проход живёт там, где идёт авторинг, а порядок
            // подъёма баз в редакторном мире ему не подчиняется. Слот остался оффсетом, и проход
            // сразу после подъёма базы доведёт его до адреса.
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
    /// Показывает провалы патча человеку. Сам патч живёт в Burst-коде и складывает их в ящик молча;
    /// без этой системы «сущности приехали раньше базы» выглядело бы как нули в полях.
    ///
    /// Стоит в бут-группе, то есть в самом начале инициализации — на кадр позже стрима секции, зато
    /// с полным сообщением.
    ///
    /// В редакторном мире система тоже нужна (иначе провалы там копились бы молча), но там она
    /// прощает «домен не поднят»: сабсцены в редакторном мире грузит Unity, когда ей удобно, а базы
    /// поднимаются чтением файла, и обогнать одно другим здесь законно. В плеере порядок наш, и
    /// приехавшая раньше базы сущность остаётся ошибкой.
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

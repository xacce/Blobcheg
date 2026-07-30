using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Entities.Serialization;

namespace Blobcheg.PatchTests
{
    /// <summary>
    /// Стенд деструктивного набора патча ссылок.
    ///
    /// База собирается ПИСАТЕЛЕМ, а не ассетами: половина набора живёт на точных адресах записей
    /// («ровно на последней», «поколение сдвинуло запись», «оффсет мимо выравнивания»), а
    /// пересборка ассетов таких раскладок не даёт и стоит секунды. Вход и выход при этом те же
    /// самые: <see cref="BlobchegWriter"/> → байты → <see cref="BlobchegBuffer"/> →
    /// <see cref="BlobchegBlob"/>, то есть ровно то, что делает бут-система потребителя.
    ///
    /// Патч дёргается двумя публичными дорогами, обе настоящие:
    /// 1. <see cref="BlobchegLiveSweep.Run"/> — живой путь (чейнджсет открытой сабсцены);
    /// 2. <see cref="SerializeUtility"/> — запись мира (обратный проход) и его чтение (прямой).
    ///
    /// Правило набора: ни один тест не разыменовывает освобождённую память и не читает по дикому
    /// адресу. Там, где сценарий про это, спрашивается РЕЕСТР, а не память.
    /// </summary>
    public abstract unsafe class PatchFixture
    {
        protected World World;
        protected EntityManager EM;

        string _dir;
        readonly List<RaisedBase> _bases = new List<RaisedBase>();
        readonly List<World> _extraWorlds = new List<World>();

        [SetUp]
        public void PatchSetUp()
        {
            // Таблицу собирает InitializeOnLoad; вызов идемпотентен и страхует запуск из CLI, где
            // порядок инициализаторов домена гарантий не даёт.
            BlobchegPatchInstall.Install();

            Assert.That(BlobchegPatchTable.IsBuilt, Is.True,
                "таблица слотов не собрана — патч не установлен, и весь набор проверял бы пустоту");

            // Реестр общий на процесс. Не почистив его, тест наследует базы соседнего.
            BlobchegBases.Clear();
            BlobchegPatchErrors.Clear();

            _dir = Path.Combine(Path.GetTempPath(), "blobcheg-patch-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);

            World = new World("blobcheg-patch-tests");
            EM = World.EntityManager;
        }

        [TearDown]
        public void PatchTearDown()
        {
            World.Dispose();

            foreach (var world in _extraWorlds)
                if (world.IsCreated)
                    world.Dispose();

            _extraWorlds.Clear();

            foreach (var raised in _bases)
                Drop(raised);

            _bases.Clear();

            BlobchegBases.Clear();
            BlobchegPatchErrors.Clear();

            try
            {
                if (Directory.Exists(_dir))
                    Directory.Delete(_dir, true);
            }
            catch (IOException)
            {
                // Мусор во временной папке ОС тест не роняет.
            }
        }

        // ------------------------------------------------------------- файл базы

        /// <summary>Файл домена, собираемый писателем. Ключ записи — её имя в тесте.</summary>
        protected sealed class DomainFile
        {
            readonly BlobchegWriter _writer;
            readonly Dictionary<string, int> _tickets = new Dictionary<string, int>();

            public DomainFile(string directory, string domain)
            {
                Domain = domain;
                _writer = BlobchegWriter.Open(directory, domain);
            }

            public string Domain { get; }

            public DomainFile Add<T>(string key, T value) where T : unmanaged
            {
                var bytes = new byte[UnsafeUtility.SizeOf<T>()];
                fixed (byte* p = bytes)
                    UnsafeUtility.CopyStructureToPtr(ref value, p);

                _tickets[key] = _writer.Append(new BlobchegRecord(
                    typeof(T).FullName, key, unchecked((uint)BurstRuntime.GetHashCode32<T>()), key, bytes));

                return this;
            }

            /// <summary>
            /// Запечатывает файл. Отладочный контур пишется по умолчанию — так живёт редактор, и
            /// только так видно, что старый путь чтения ловит то, чего не ловит новый.
            /// </summary>
            public DomainFile Seal(bool debug = true)
            {
                _writer.Flush(debug);
                return this;
            }

            public uint this[string key] => _writer.OffsetOf(_tickets[key]);

            public byte[] Bytes() => File.ReadAllBytes(_writer.FilePath);
        }

        /// <summary>Поднятая база: сам блоб плюс адрес и длина, чтобы тест мог считать сам.</summary>
        protected sealed class RaisedBase
        {
            public BlobchegBlob Blob;
            public ulong Key;
            public ulong Ptr;
            public int Length;
            public bool Dropped;

            /// <summary>Адрес записи по её оффсету — то, во что патч ОБЯЗАН превратить слот.</summary>
            public ulong AddressOf(uint offset) => Ptr + offset;
        }

        protected DomainFile Domain(string name) => new DomainFile(_dir, name);

        protected RaisedBase Raise(DomainFile file)
        {
            var buffer = BlobchegBuffer.From(file.Bytes(), Allocator.Persistent);
            var raised = new RaisedBase
            {
                Blob = new BlobchegBlob(buffer, file.Domain),
                Ptr = (ulong)buffer.Ptr,
                Length = buffer.Length,
            };

            raised.Key = raised.Blob.DomainKey;
            _bases.Add(raised);
            return raised;
        }

        /// <summary>Снимает базу с учёта и освобождает буфер — как <c>Dispose</c> у потребителя.</summary>
        protected static void Drop(RaisedBase raised)
        {
            if (raised.Dropped)
                return;

            raised.Blob.Dispose();
            raised.Dropped = true;
        }

        /// <summary>Горячая база с пушкой и бронёй. Раскладка: тип по FullName, значит броня первой.</summary>
        protected DomainFile HotFile(float ammo = 30f, int rpm = 600, float hp = 100f, int plates = 3)
            => Domain(nameof(IPatchHot))
                .Add("gun", new PatchGun { Ammo = ammo, Rpm = rpm })
                .Add("armor", new PatchArmor { Hp = hp, Plates = plates })
                .Seal();

        // ------------------------------------------------------------- патч

        /// <summary>Живой путь: чейнджсет лёг, ссылки сырые. Бросает по первому провалу.</summary>
        protected void Patch() => BlobchegLiveSweep.Run(EM);

        protected void Patch(World world) => BlobchegLiveSweep.Run(world.EntityManager);

        // ------------------------------------------------------------- сериализация

        /// <summary>Запись мира в память. Обратный проход идёт внутри — по копии чанка.</summary>
        protected byte[] Save() => Save(World);

        protected byte[] Save(World world)
        {
            world.EntityManager.CompleteAllTrackedJobs();

            using (var writer = new MemoryBinaryWriter())
            {
                SerializeUtility.SerializeWorld(world.EntityManager, writer, out _);

                var bytes = new byte[writer.Length];
                fixed (byte* dst = bytes)
                    UnsafeUtility.MemCpy(dst, writer.Data, writer.Length);

                return bytes;
            }
        }

        /// <summary>Чтение мира. Патч загрузки срабатывает внутри, ровно как на секции сабсцены.</summary>
        protected World Load(byte[] bytes, string name = "blobcheg-patch-loaded")
        {
            var world = new World(name);
            _extraWorlds.Add(world);

            fixed (byte* p = bytes)
            {
                using (var reader = new MemoryBinaryReader(p, bytes.Length))
                {
                    var transaction = world.EntityManager.BeginExclusiveEntityTransaction();
                    SerializeUtility.DeserializeWorld(transaction, reader);
                    world.EntityManager.EndExclusiveEntityTransaction();
                }
            }

            return world;
        }

        /// <summary>
        /// Мир, прочитанный БЕЗ единой поднятой базы. Патч загрузки при этом слот не трогает, и в
        /// нём остаётся ровно то, что лежало в файле, — единственный публичный способ увидеть,
        /// какое число обратный проход туда положил.
        /// </summary>
        protected World LoadRaw(byte[] bytes)
        {
            foreach (var raised in _bases)
                Drop(raised);

            BlobchegBases.Clear();

            var world = Load(bytes, "blobcheg-patch-raw");
            BlobchegPatchErrors.Clear();
            return world;
        }

        /// <summary>Ищет в сериализованном мире восьмибайтовое слово. Проверка обещания «на диск едет оффсет».</summary>
        protected static bool Contains(byte[] bytes, ulong word)
        {
            var wanted = BitConverter.GetBytes(word);

            for (var i = 0; i + 8 <= bytes.Length; i++)
            {
                var hit = true;
                for (var k = 0; k < 8; k++)
                {
                    if (bytes[i + k] == wanted[k])
                        continue;

                    hit = false;
                    break;
                }

                if (hit)
                    return true;
            }

            return false;
        }

        // ------------------------------------------------------------- сущности

        protected Entity Gun(uint offset)
        {
            var entity = EM.CreateEntity();
            EM.AddComponentData(entity, new GunRef { Gun = new BlobchegReference<PatchGun>(offset) });
            return entity;
        }

        protected ulong SlotOf(Entity entity) => EM.GetComponentData<GunRef>(entity).Gun.Data.Value;

        protected static ulong SlotOf(World world, Entity entity)
            => world.EntityManager.GetComponentData<GunRef>(entity).Gun.Data.Value;

        /// <summary>Единственная сущность мира с этим компонентом. Иначе тест проверял бы не то.</summary>
        protected static Entity Single<T>(World world) where T : unmanaged, IComponentData
            => SingleOf(world, ComponentType.ReadOnly<T>(), typeof(T).Name);

        /// <summary>
        /// То же для элемента буфера: <c>IBufferElementData</c> не наследует <c>IComponentData</c>,
        /// и общий констрейнт его не берёт.
        /// </summary>
        protected static Entity SingleBuffer<T>(World world) where T : unmanaged, IBufferElementData
            => SingleOf(world, ComponentType.ReadOnly<T>(), typeof(T).Name);

        static Entity SingleOf(World world, ComponentType componentType, string name)
        {
            var query = world.EntityManager.CreateEntityQuery(componentType);
            var entities = query.ToEntityArray(Allocator.Temp);

            Assert.That(entities.Length, Is.EqualTo(1), $"в мире ожидалась одна сущность с {name}");

            var entity = entities[0];
            entities.Dispose();
            return entity;
        }

        /// <summary>Копия <c>ref readonly</c>-возврата: иначе чтение записи нечем «использовать».</summary>
        protected static T Copy<T>(in T value) where T : unmanaged => value;
    }
}

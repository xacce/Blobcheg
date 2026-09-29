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
    // Bases come from the writer, not assets: tests need exact record layouts; rebuilds cost seconds.
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
            BlobchegPatchInstall.Install(); // idempotent: CLI runs do not guarantee domain initialiser order

            Assert.That(BlobchegPatchTable.IsBuilt, Is.True,
                "the slot table is not built — the patch is not installed, and the whole set would be checking emptiness");

            BlobchegBases.Clear(); // process-wide registry: else a test inherits its neighbour's bases
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
                // Leftovers in the OS temp folder must not fail the test.
            }
        }

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

            // Debug contour on by default, as in the editor: exposes what only the old read path catches.
            public DomainFile Seal(bool debug = true)
            {
                _writer.Flush(debug);
                return this;
            }

            public uint this[string key] => _writer.OffsetOf(_tickets[key]);

            public byte[] Bytes() => File.ReadAllBytes(_writer.FilePath);
        }

        protected sealed class RaisedBase
        {
            public BlobchegBlob Blob;
            public ulong Key;
            public ulong Ptr;
            public int Length;
            public bool Dropped;

            public ulong AddressOf(uint offset) => Ptr + offset; // what the patch must turn a slot into
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

        protected static void Drop(RaisedBase raised)
        {
            if (raised.Dropped)
                return;

            raised.Blob.Dispose();
            raised.Dropped = true;
        }

        // Records are laid out by type FullName, so the armor comes first.
        protected DomainFile HotFile(float ammo = 30f, int rpm = 600, float hp = 100f, int plates = 3)
            => Domain(nameof(IPatchHot))
                .Add("gun", new PatchGun { Ammo = ammo, Rpm = rpm })
                .Add("armor", new PatchArmor { Hp = hp, Plates = plates })
                .Seal();

        protected void Patch() => BlobchegLiveSweep.Run(EM); // live path; throws on the first failure

        protected void Patch(World world) => BlobchegLiveSweep.Run(world.EntityManager);

        protected byte[] Save() => Save(World); // the reverse pass runs inside, over a chunk copy

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

        protected World Load(byte[] bytes, string name = "blobcheg-patch-loaded")
        {
            var world = new World(name);
            _extraWorlds.Add(world);

            fixed (byte* p = bytes)
            {
                using (var reader = new MemoryBinaryReader(p, bytes.Length))
                {
                    var transaction = world.EntityManager.BeginExclusiveEntityTransaction();
                    SerializeUtility.DeserializeWorld(transaction, reader); // load patch fires here, as on a section
                    world.EntityManager.EndExclusiveEntityTransaction();
                }
            }

            return world;
        }

        // No base loaded: the load patch leaves slots alone, exposing the number the reverse pass wrote.
        protected World LoadRaw(byte[] bytes)
        {
            foreach (var raised in _bases)
                Drop(raised);

            BlobchegBases.Clear();

            var world = Load(bytes, "blobcheg-patch-raw");
            BlobchegPatchErrors.Clear();
            return world;
        }

        protected static bool Contains(byte[] bytes, ulong word) // proves an offset travels to disk
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

        protected Entity Gun(uint offset)
        {
            var entity = EM.CreateEntity();
            EM.AddComponentData(entity, new GunRef { Gun = new BlobchegReference<PatchGun>(offset) });
            return entity;
        }

        protected ulong SlotOf(Entity entity) => EM.GetComponentData<GunRef>(entity).Gun.Data.Value;

        protected static ulong SlotOf(World world, Entity entity)
            => world.EntityManager.GetComponentData<GunRef>(entity).Gun.Data.Value;

        protected static Entity Single<T>(World world) where T : unmanaged, IComponentData
            => SingleOf(world, ComponentType.ReadOnly<T>(), typeof(T).Name);

        // IBufferElementData does not inherit IComponentData, so the Single<T> constraint rejects it.
        protected static Entity SingleBuffer<T>(World world) where T : unmanaged, IBufferElementData
            => SingleOf(world, ComponentType.ReadOnly<T>(), typeof(T).Name);

        static Entity SingleOf(World world, ComponentType componentType, string name)
        {
            var query = world.EntityManager.CreateEntityQuery(componentType);
            var entities = query.ToEntityArray(Allocator.Temp);

            Assert.That(entities.Length, Is.EqualTo(1), $"one entity with {name} was expected in the world");

            var entity = entities[0];
            entities.Dispose();
            return entity;
        }

        protected static T Copy<T>(in T value) where T : unmanaged => value; // gives a ref readonly read a use
    }
}

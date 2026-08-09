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
    /// The rig of the destructive set for the reference patch.
    ///
    /// The base is assembled by the WRITER and not from assets: half the set lives on exact record
    /// addresses ("exactly on the last one", "a generation moved the record", "an offset off the
    /// alignment"), and a rebuild from assets does not produce such layouts and costs seconds. The input
    /// and the output are the same all the same: <see cref="BlobchegWriter"/> → bytes →
    /// <see cref="BlobchegBuffer"/> → <see cref="BlobchegBlob"/>, that is, exactly what a consumer's
    /// boot system does.
    ///
    /// The patch is pulled along two public roads, both of them real:
    /// 1. <see cref="BlobchegLiveSweep.Run"/> — the live path (the change set of an open subscene);
    /// 2. <see cref="SerializeUtility"/> — writing a world (the reverse pass) and reading it (the forward one).
    ///
    /// The rule of the set: not a single test dereferences freed memory or reads at a wild address.
    /// Where the scenario is about that, the REGISTRY is asked and not the memory.
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
            // The table is built by InitializeOnLoad; the call is idempotent and insures a run from the
            // CLI, where the order of the domain initialisers gives no guarantees.
            BlobchegPatchInstall.Install();

            Assert.That(BlobchegPatchTable.IsBuilt, Is.True,
                "the slot table is not built — the patch is not installed, and the whole set would be checking emptiness");

            // The registry is shared by the process. Without cleaning it, a test inherits the bases of its neighbour.
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
                // Rubbish in the OS temp folder does not fail the test.
            }
        }

        // ------------------------------------------------------------- the base file

        /// <summary>A domain file assembled by the writer. The key of a record is its name in the test.</summary>
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
            /// Seals the file. The debug contour is written by default — that is how the editor lives,
            /// and only that way is it visible that the old read path catches what the new one does not.
            /// </summary>
            public DomainFile Seal(bool debug = true)
            {
                _writer.Flush(debug);
                return this;
            }

            public uint this[string key] => _writer.OffsetOf(_tickets[key]);

            public byte[] Bytes() => File.ReadAllBytes(_writer.FilePath);
        }

        /// <summary>A loaded base: the blob itself plus the address and the length, so the test can compute for itself.</summary>
        protected sealed class RaisedBase
        {
            public BlobchegBlob Blob;
            public ulong Key;
            public ulong Ptr;
            public int Length;
            public bool Dropped;

            /// <summary>The address of a record by its offset — what the patch is OBLIGED to turn a slot into.</summary>
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

        /// <summary>Takes the base off the register and frees the buffer — like a consumer's <c>Dispose</c>.</summary>
        protected static void Drop(RaisedBase raised)
        {
            if (raised.Dropped)
                return;

            raised.Blob.Dispose();
            raised.Dropped = true;
        }

        /// <summary>The hot base with a gun and armor. The layout: type by FullName, so the armor comes first.</summary>
        protected DomainFile HotFile(float ammo = 30f, int rpm = 600, float hp = 100f, int plates = 3)
            => Domain(nameof(IPatchHot))
                .Add("gun", new PatchGun { Ammo = ammo, Rpm = rpm })
                .Add("armor", new PatchArmor { Hp = hp, Plates = plates })
                .Seal();

        // ------------------------------------------------------------- the patch

        /// <summary>The live path: the change set landed, the references are raw. Throws on the first failure.</summary>
        protected void Patch() => BlobchegLiveSweep.Run(EM);

        protected void Patch(World world) => BlobchegLiveSweep.Run(world.EntityManager);

        // ------------------------------------------------------------- serialisation

        /// <summary>Writing a world into memory. The reverse pass runs inside — over a copy of the chunk.</summary>
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

        /// <summary>Reading a world. The load patch fires inside, exactly as on a subscene section.</summary>
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
        /// A world read WITHOUT a single loaded base. The load patch does not touch the slot then, and
        /// exactly what lay in the file stays in it — the only public way to see which number the
        /// reverse pass put there.
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

        /// <summary>Looks for an eight-byte word in a serialised world. A check of the promise "an offset travels to disk".</summary>
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

        // ------------------------------------------------------------- entities

        protected Entity Gun(uint offset)
        {
            var entity = EM.CreateEntity();
            EM.AddComponentData(entity, new GunRef { Gun = new BlobchegReference<PatchGun>(offset) });
            return entity;
        }

        protected ulong SlotOf(Entity entity) => EM.GetComponentData<GunRef>(entity).Gun.Data.Value;

        protected static ulong SlotOf(World world, Entity entity)
            => world.EntityManager.GetComponentData<GunRef>(entity).Gun.Data.Value;

        /// <summary>The only entity in the world with this component. Otherwise the test would check the wrong thing.</summary>
        protected static Entity Single<T>(World world) where T : unmanaged, IComponentData
            => SingleOf(world, ComponentType.ReadOnly<T>(), typeof(T).Name);

        /// <summary>
        /// The same for a buffer element: <c>IBufferElementData</c> does not inherit
        /// <c>IComponentData</c>, and the common constraint does not take it.
        /// </summary>
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

        /// <summary>A copy of a <c>ref readonly</c> return: otherwise there is nothing to "use" the read record with.</summary>
        protected static T Copy<T>(in T value) where T : unmanaged => value;
    }
}

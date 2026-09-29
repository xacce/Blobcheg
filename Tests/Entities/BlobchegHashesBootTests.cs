using System.Diagnostics;
using Blobcheg.Authoring;
using NUnit.Framework;
using Unity.Entities;

namespace Blobcheg.Tests
{
    public interface ITestBootHashData
    {
    }

    public struct TestBootHashRecord : ITestBootHashData
    {
        public ulong Self;
    }

    [Blobcheg(typeof(ITestBootHashData), "boot")]
    public partial struct TestBootHashDb
    {
    }

    [BlobchegRouter] // a table and its router must live in one compilation
    public partial struct TestBootRouter
    {
    }

    [BlobchegHashes(typeof(TestBootRouter), AutoLoad = true)] // emits TestBootHashesBootSystem, or no build
    [DisableAutoCreation]
    public partial struct TestBootHashes
    {
    }

    // The table is empty here: this proves the load; the lookup is proven in Blobcheg.Hashes.Tests.
    public sealed class BlobchegHashesBootTests
    {
        [Test]
        public void The_boot_system_loads_the_table_onto_the_register()
        {
            BlobchegBuild.RebuildAll();

            var world = new World("blobcheg-hashes-boot-tests");
            try
            {
                var system = world.CreateSystem<TestBootHashesBootSystem>();

                var clock = Stopwatch.StartNew();
                while (!BlobchegBases.Has(TestBootHashes.HashesKey) && clock.ElapsedMilliseconds < 5000)
                {
                    system.Update(world.Unmanaged);
                    System.Threading.Thread.Sleep(1);
                }

                Assert.That(BlobchegBases.Has(TestBootHashes.HashesKey), Is.True,
                    "the boot system is obliged to put the table onto the register within five seconds");

                var table = TestBootHashes.Resident;
                Assert.That(table.IsCreated, Is.True);
                Assert.That(table.Tag, Is.EqualTo(BlobchegNaming.TagOf(TestBootHashes.RouterName)));
                Assert.That(TestBootHashes.HashesKey,
                    Is.EqualTo(BlobchegNaming.NameHash(TestBootHashes.FileIdentity)),
                    "the generator's fnv1a drifted from BlobchegNaming.NameHash — Resident looks up a key nobody registers");
            }
            finally
            {
                world.Dispose();
            }
        }

        [Test]
        public void The_table_is_re_read_under_a_live_world()
        {
            BlobchegBuild.RebuildAll();

            var world = new World("blobcheg-hashes-reraise-tests");
            try
            {
                var system = world.CreateSystem<TestBootHashesBootSystem>();

                var clock = Stopwatch.StartNew();
                while (!BlobchegBases.Has(TestBootHashes.HashesKey) && clock.ElapsedMilliseconds < 5000)
                {
                    system.Update(world.Unmanaged);
                    System.Threading.Thread.Sleep(1);
                }

                Assert.That(BlobchegBases.Has(TestBootHashes.HashesKey), Is.True, "the table did not load — there is nothing further to check");

                BlobchegFileVersions.Bump(TestBootHashes.FileName); // what an editor rebuild ends with
                system.Update(world.Unmanaged);

                var table = TestBootHashes.Resident;
                Assert.That(table.IsCreated, Is.True,
                    "Resident is obliged to hand out the new blob and not the freed old one");
            }
            finally
            {
                world.Dispose();
            }
        }

        [Test]
        public void The_boot_system_of_the_table_stands_in_the_load_group()
        {
            var system = typeof(TestBootHashesBootSystem);

            Assert.That(system.GetCustomAttributes(typeof(DisableAutoCreationAttribute), false), Is.Not.Empty,
                "a [DisableAutoCreation] on the table is obliged to end up on its boot system");

            var inGroup = (UpdateInGroupAttribute)system.GetCustomAttributes(typeof(UpdateInGroupAttribute), false)[0];
            Assert.That(inGroup.GroupType, Is.EqualTo(typeof(BlobchegBootGroup)));
        }
    }
}

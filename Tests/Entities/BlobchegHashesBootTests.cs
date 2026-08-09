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

    /// <summary>The router of this assembly. Its own, because a table and a router must be of one compilation.</summary>
    [BlobchegRouter]
    public partial struct TestBootRouter
    {
    }

    /// <summary>
    /// A table declared <c>IComponentData</c>: the generator emits a
    /// <c>TestBootHashesBootSystem</c> for it. If it did not, this file does not build.
    /// </summary>
    [BlobchegHashes(typeof(TestBootRouter))]
    [DisableAutoCreation]
    public partial struct TestBootHashes : IComponentData
    {
    }

    /// <summary>
    /// This router has no nodes in the project and the table is assembled empty — what has to be proven
    /// here is the load and not the lookup: the lookup is proven in Blobcheg.Hashes.Tests, which has its
    /// own nodes.
    /// </summary>
    public sealed class BlobchegHashesBootTests
    {
        [Test]
        public void The_boot_system_loads_the_table_into_a_singleton()
        {
            BlobchegBuild.RebuildAll();

            var world = new World("blobcheg-hashes-boot-tests");
            try
            {
                var system = world.CreateSystem<TestBootHashesBootSystem>();
                var query = world.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<TestBootHashes>());

                var clock = Stopwatch.StartNew();
                while (query.CalculateEntityCount() == 0 && clock.ElapsedMilliseconds < 5000)
                {
                    system.Update(world.Unmanaged);
                    System.Threading.Thread.Sleep(1);
                }

                Assert.That(query.CalculateEntityCount(), Is.EqualTo(1),
                    "the boot system is obliged to put the table down as a singleton within five seconds");

                var table = query.GetSingleton<TestBootHashes>();
                Assert.That(table.IsCreated, Is.True);
                Assert.That(table.Tag, Is.EqualTo(BlobchegNaming.TagOf(TestBootHashes.RouterName)));
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
                var query = world.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<TestBootHashes>());

                var clock = Stopwatch.StartNew();
                while (query.CalculateEntityCount() == 0 && clock.ElapsedMilliseconds < 5000)
                {
                    system.Update(world.Unmanaged);
                    System.Threading.Thread.Sleep(1);
                }

                Assert.That(query.CalculateEntityCount(), Is.EqualTo(1), "the table did not load — there is nothing further to check");

                // A rebuild in the editor ends with exactly this: the file number is bumped.
                BlobchegFileVersions.Bump(TestBootHashes.FileName);
                system.Update(world.Unmanaged);

                var table = query.GetSingleton<TestBootHashes>();
                Assert.That(table.IsCreated, Is.True,
                    "the singleton is obliged to hold the new blob and not the freed old one");
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

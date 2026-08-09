using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Blobcheg.Authoring;
using NUnit.Framework;
using Unity.Entities;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blobcheg.Tests
{
    public interface ITestBootData
    {
    }

    public struct TestBootRecord : ITestBootData
    {
        public int Value;
    }

    /// <summary>
    /// A base declared <c>IComponentData</c>: the generator emits a <c>TestBootDbBootSystem</c> boot
    /// system for it. If it did not, this file does not build.
    ///
    /// The <c>[DisableAutoCreation]</c> travels onto the emitted system: a test base has no business in
    /// the consumer's default world, all the more so since its file does not exist yet on a fresh
    /// checkout. The test creates a world of its own for it.
    /// </summary>
    [Blobcheg(typeof(ITestBootData))]
    [DisableAutoCreation]
    public partial struct TestBootDb : IComponentData
    {
    }

    /// <summary>
    /// The boot system emitted by the generator. A world of its own is created: what has to be proven is
    /// the system itself and not the order in which the editor's default world created it.
    /// </summary>
    public sealed unsafe class BlobchegBootTests
    {
        [Test]
        public void A_rebuild_under_a_live_world_reaches_the_singleton()
        {
            BlobchegBuild.RebuildAll();

            var world = new World("blobcheg-boot-reraise-tests");
            try
            {
                var system = world.CreateSystem<TestBootDbBootSystem>();
                var query = world.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<TestBootDb>());

                var clock = Stopwatch.StartNew();
                while (query.CalculateEntityCount() == 0 && clock.ElapsedMilliseconds < 5000)
                {
                    system.Update(world.Unmanaged);
                    System.Threading.Thread.Sleep(1);
                }

                Assert.That(query.CalculateEntityCount(), Is.EqualTo(1), "the base did not load — there is nothing further to check");

                var key = BlobchegNaming.NameHash(TestBootDb.DomainName);
                Assert.That(BlobchegBases.TryGet(key, out var before, out var length), Is.True);

                // A rebuild in the editor ends with exactly this: the file number is bumped. Watching it
                // after that is the business of whoever loaded the base.
                BlobchegFileVersions.Bump(TestBootDb.FileName);
                system.Update(world.Unmanaged);

                Assert.That(BlobchegBases.TryGet(key, out var after, out var lengthAfter), Is.True);
                Assert.That((ulong)after, Is.Not.EqualTo((ulong)before),
                    "the file was rewritten — the world is obliged to end up with the NEW buffer and not the old bytes");
                Assert.That(lengthAfter, Is.EqualTo(length), "the file is the same, so the length is the same");

                var database = query.GetSingleton<TestBootDb>();
                Assert.That(database.IsCreated, Is.True, "the singleton is obliged to hold the new blob and not the freed old one");
                Assert.That(database.Length, Is.EqualTo(lengthAfter));

                // A second update without a rebuild touches nothing: otherwise the base would be re-read every frame.
                system.Update(world.Unmanaged);
                Assert.That(BlobchegBases.TryGet(key, out var idle, out _), Is.True);
                Assert.That((ulong)idle, Is.EqualTo((ulong)after));
            }
            finally
            {
                world.Dispose();
            }
        }

        [Test]
        public void A_broken_file_is_rejected_once_and_repaired_by_a_rebuild()
        {
            BlobchegBuild.RebuildAll();

            var path = Path.Combine(
                Application.streamingAssetsPath, BlobchegNaming.DefaultFolder, TestBootDb.FileName);
            var sane = File.ReadAllBytes(path);

            var world = new World("blobcheg-boot-broken-tests");
            try
            {
                // The format version lies in the header right after the magic. Three is the package's
                // previous format: exactly such a file was rejected by the reader when the updated
                // package met old .bcheg files.
                var broken = (byte[])sane.Clone();
                broken[4] = 3;
                File.WriteAllBytes(path, broken);

                LogAssert.Expect(LogType.Exception, new Regex("format version 3"));

                var system = world.CreateSystem<TestBootDbBootSystem>();
                var query = world.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<TestBootDb>());

                // The frames keep going, and the failure is obliged to stay a single one. Otherwise the
                // real cause is drowned by a retelling of its consequences: the read was taken away, and
                // every following Poll runs into that.
                var clock = Stopwatch.StartNew();
                while (clock.ElapsedMilliseconds < 2000)
                {
                    system.Update(world.Unmanaged);
                    System.Threading.Thread.Sleep(1);
                }

                LogAssert.NoUnexpectedReceived();

                // The rebuild rewrote the file — now the load is obliged to run again, without a domain
                // reload: otherwise a world that broke is repaired by nothing at all.
                File.WriteAllBytes(path, sane);
                BlobchegFileVersions.Bump(TestBootDb.FileName);

                clock.Restart();
                while (query.CalculateEntityCount() == 0 && clock.ElapsedMilliseconds < 5000)
                {
                    system.Update(world.Unmanaged);
                    System.Threading.Thread.Sleep(1);
                }

                Assert.That(query.CalculateEntityCount(), Is.EqualTo(1),
                    "the repaired file is obliged to reach the singleton");
            }
            finally
            {
                File.WriteAllBytes(path, sane);
                world.Dispose();
            }
        }

        /// <summary>
        /// The path to the test base file. Wiping and corrupting it is allowed: the saved bytes are put
        /// back in the finally, and the rebuild assembles it again anyway.
        /// </summary>
        static string PathOfDatabase()
            => Path.Combine(Application.streamingAssetsPath, BlobchegNaming.DefaultFolder, TestBootDb.FileName);

        /// <summary>
        /// Collects the log over the run. The warning here is the subject of the check, and
        /// <c>LogAssert</c> on warnings proves only "it arrived at least once"; we also need "exactly
        /// once".
        /// </summary>
        sealed class LogTrap : System.IDisposable
        {
            readonly List<(LogType type, string text)> _messages = new List<(LogType, string)>();

            public LogTrap() => Application.logMessageReceived += Take;

            void Take(string condition, string trace, LogType type) => _messages.Add((type, condition));

            public int Notifications => _messages.Count(m =>
                m.type == LogType.Warning && m.text.Contains("a notification and not a problem"));

            public IEnumerable<string> Loud => _messages
                .Where(m => m.type == LogType.Error || m.type == LogType.Exception || m.type == LogType.Assert)
                .Select(m => m.text);

            public void Dispose() => Application.logMessageReceived -= Take;
        }

        static void Spin(SystemHandle system, World world, int ms, System.Func<bool> until = null)
        {
            var clock = Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < ms)
            {
                system.Update(world.Unmanaged);
                if (until != null && until())
                    return;

                System.Threading.Thread.Sleep(1);
            }
        }

        /// <summary>
        /// The base file is not there yet — that is what a domain that arrived with a pull before its own
        /// rebuild looks like. A red error here lies: there is nothing to fix and the load will run by
        /// itself.
        /// </summary>
        [Test]
        public void A_missing_file_is_rejected_with_a_warning_and_repaired_by_a_rebuild()
        {
            BlobchegBuild.RebuildAll();

            var path = PathOfDatabase();
            var sane = File.ReadAllBytes(path);

            var world = new World("blobcheg-boot-missing-tests");
            using (var log = new LogTrap())
            {
                try
                {
                    File.Delete(path);

                    var system = world.CreateSystem<TestBootDbBootSystem>();
                    var query = world.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<TestBootDb>());

                    Spin(system, world, 1000);

                    Assert.That(log.Loud, Is.Empty, "a transient moment is not an error, there must be no red in the log");
                    Assert.That(log.Notifications, Is.EqualTo(1),
                        "there is obliged to be a warning, and exactly one: the frames keep going and there is nothing to say twice");
                    Assert.That(query.CalculateEntityCount(), Is.Zero, "there is nothing to load yet");

                    // The rebuild wrote the file and bumped its number — from there the system is obliged to manage on its own.
                    File.WriteAllBytes(path, sane);
                    BlobchegFileVersions.Bump(TestBootDb.FileName);

                    Spin(system, world, 5000, () => query.CalculateEntityCount() == 1);

                    Assert.That(query.CalculateEntityCount(), Is.EqualTo(1),
                        "the written file is obliged to reach the singleton without a domain reload");
                }
                finally
                {
                    File.WriteAllBytes(path, sane);
                    world.Dispose();
                }
            }
        }

        /// <summary>
        /// The file was caught mid-rewrite: the reader learned the length from the new header while the
        /// bytes came from the old one. A frame later the same read goes through — so this is a
        /// notification too.
        /// </summary>
        [Test]
        public void A_file_caught_mid_rewrite_is_rejected_with_a_warning_and_repaired_by_a_rebuild()
        {
            BlobchegBuild.RebuildAll();

            var path = PathOfDatabase();
            var sane = File.ReadAllBytes(path);

            var world = new World("blobcheg-boot-torn-tests");
            using (var log = new LogTrap())
            {
                try
                {
                    File.WriteAllBytes(path, sane.Concat(new byte[] { 0 }).ToArray());

                    var system = world.CreateSystem<TestBootDbBootSystem>();
                    var query = world.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<TestBootDb>());

                    Spin(system, world, 1000);

                    Assert.That(log.Loud, Is.Empty, "an unfinished file is a moment and not a breakage");
                    Assert.That(log.Notifications, Is.EqualTo(1));
                    Assert.That(query.CalculateEntityCount(), Is.Zero);

                    File.WriteAllBytes(path, sane);
                    BlobchegFileVersions.Bump(TestBootDb.FileName);

                    Spin(system, world, 5000, () => query.CalculateEntityCount() == 1);

                    Assert.That(query.CalculateEntityCount(), Is.EqualTo(1));
                }
                finally
                {
                    File.WriteAllBytes(path, sane);
                    world.Dispose();
                }
            }
        }

        /// <summary>
        /// The same under a live world: the base is already loaded and the reload landed in the middle of
        /// a rewrite. The file number is obliged to stay unseen — otherwise there will be nothing to
        /// re-read until the next rebuild and the world will quietly stay on the old bytes.
        /// </summary>
        [Test]
        public void A_reload_on_an_unfinished_file_retries_by_itself()
        {
            BlobchegBuild.RebuildAll();

            var path = PathOfDatabase();
            var sane = File.ReadAllBytes(path);
            var key = BlobchegNaming.NameHash(TestBootDb.DomainName);

            var world = new World("blobcheg-boot-torn-reraise-tests");
            using (var log = new LogTrap())
            {
                try
                {
                    var system = world.CreateSystem<TestBootDbBootSystem>();
                    var query = world.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<TestBootDb>());

                    Spin(system, world, 5000, () => query.CalculateEntityCount() == 1);
                    Assert.That(query.CalculateEntityCount(), Is.EqualTo(1), "the base did not load — there is nothing further to check");
                    Assert.That(BlobchegBases.TryGet(key, out var before, out _), Is.True);

                    // The rebuild "rewrites" the file: the number is bumped while the bytes on disk are unfinished.
                    File.WriteAllBytes(path, sane.Concat(new byte[] { 0 }).ToArray());
                    BlobchegFileVersions.Bump(TestBootDb.FileName);

                    Spin(system, world, 1000);

                    Assert.That(log.Loud, Is.Empty);
                    Assert.That(log.Notifications, Is.EqualTo(1), "one warning per streak, not per frame");
                    Assert.That(BlobchegBases.TryGet(key, out var held, out _), Is.True);
                    Assert.That((ulong)held, Is.EqualTo((ulong)before), "the world keeps running on the old base while the new one is unfinished");

                    // The file is finished and the number is NOT BUMPED AGAIN: that rebuild already
                    // happened, and retrying is the system's own business.
                    File.WriteAllBytes(path, sane);

                    Spin(system, world, 5000,
                        () => BlobchegBases.TryGet(key, out var now, out _) && (ulong)now != (ulong)before);

                    Assert.That(BlobchegBases.TryGet(key, out var after, out _), Is.True);
                    Assert.That((ulong)after, Is.Not.EqualTo((ulong)before),
                        "the finished file is obliged to reach the world by itself — otherwise it would quietly stay on yesterday's bytes");
                    Assert.That(query.GetSingleton<TestBootDb>().IsCreated, Is.True);
                }
                finally
                {
                    File.WriteAllBytes(path, sane);
                    world.Dispose();
                }
            }
        }

        [Test]
        public void The_boot_system_exists_in_the_editor_world_too()
        {
            var filter = (WorldSystemFilterAttribute)typeof(TestBootDbBootSystem)
                .GetCustomAttributes(typeof(WorldSystemFilterAttribute), false)[0];

            Assert.That(filter.FilterFlags & WorldSystemFilterFlags.Editor, Is.Not.EqualTo(default(WorldSystemFilterFlags)),
                "without this flag there is no base in the editor world, and any patch pass there runs into \"the domain is not loaded\"");
            Assert.That(filter.FilterFlags & WorldSystemFilterFlags.Default, Is.Not.EqualTo(default(WorldSystemFilterFlags)),
                "while the game world went nowhere");
        }

        [Test]
        public void The_boot_system_loads_the_base_into_a_singleton()
        {
            // The base file is obliged to lie on disk: a writer is opened for every declared domain, so
            // an empty ITestBootData gets assembled too.
            BlobchegBuild.RebuildAll();

            var world = new World("blobcheg-boot-tests");
            try
            {
                var system = world.CreateSystem<TestBootDbBootSystem>();
                var query = world.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<TestBootDb>());

                var clock = Stopwatch.StartNew();
                while (query.CalculateEntityCount() == 0 && clock.ElapsedMilliseconds < 5000)
                {
                    system.Update(world.Unmanaged);
                    System.Threading.Thread.Sleep(1);
                }

                Assert.That(query.CalculateEntityCount(), Is.EqualTo(1),
                    "the boot system is obliged to put the base down as a singleton within five seconds");

                var database = query.GetSingleton<TestBootDb>();
                Assert.That(database.IsCreated, Is.True);
                Assert.That(database.Length, Is.GreaterThanOrEqualTo(BlobchegFormat.HeaderSize));
            }
            finally
            {
                // The world is disposed — the system's OnDestroy gives the base buffer back.
                world.Dispose();
            }
        }

        [Test]
        public void The_auto_creation_ban_travels_from_the_base_onto_the_emitted_system()
        {
            var system = typeof(TestBootDbBootSystem);

            Assert.That(system.GetCustomAttributes(typeof(DisableAutoCreationAttribute), false), Is.Not.Empty,
                "a [DisableAutoCreation] on the base is obliged to end up on its boot system, otherwise the " +
                "consumer's default world loads a foreign base");

            var inGroup = (UpdateInGroupAttribute)system.GetCustomAttributes(typeof(UpdateInGroupAttribute), false)[0];
            Assert.That(inGroup.GroupType, Is.EqualTo(typeof(BlobchegBootGroup)));
        }

        [Test]
        public void The_load_group_stands_before_the_initialisation_command_buffer()
        {
            var group = typeof(BlobchegBootGroup);

            var inGroup = (UpdateInGroupAttribute)group.GetCustomAttributes(typeof(UpdateInGroupAttribute), false)[0];
            Assert.That(inGroup.GroupType, Is.EqualTo(typeof(InitializationSystemGroup)));
            Assert.That(inGroup.OrderFirst, Is.True);

            var before = (UpdateBeforeAttribute)group.GetCustomAttributes(typeof(UpdateBeforeAttribute), false)[0];
            Assert.That(before.SystemType, Is.EqualTo(typeof(BeginInitializationEntityCommandBufferSystem)),
                "the bases are obliged to load before the first command buffer of the frame is played back");
        }
    }
}

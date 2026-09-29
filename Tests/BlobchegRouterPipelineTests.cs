using System;
using System.IO;
using System.Linq;
using Blobcheg.Authoring;
using NUnit.Framework;
using Unity.Collections;
using UnityEditor;
using UnityEngine;

namespace Blobcheg.Tests
{
    /// <summary>The second domain of the test — needed so that the router has more than one bit.</summary>
    public interface ITestColdData
    {
    }

    public struct TestColdInfo : ITestColdData
    {
        /// <summary>Its own id, put by the node straight into the record: proof that it is known before the write.</summary>
        public uint SelfId;

        public int Tier;
    }

    [Blobcheg(typeof(ITestColdData), "cold", Router = typeof(TestGameRouter))]
    public partial struct TestColdDb
    {
    }

    /// <summary>
    /// The ordinary router of the test: it hands out the row numbers itself. The bases name it
    /// explicitly — a second, deterministic one (<c>TestFixedRouter</c>) lives next to it, and there is
    /// nobody to choose for them.
    /// </summary>
    [BlobchegRouter]
    public partial struct TestGameRouter
    {
    }

    /// <summary>A node in both bases: a router row with two bits.</summary>
    public sealed class TestModuleNodeSo : BlobchegNodeSo
    {
        public int tier = 3;

        public override Type[] OutTypes => new[] { typeof(ITestCombatData), typeof(ITestColdData) };

        public override void Write(ref BlobchegNodeWriter writer)
        {
            writer.Add(new TestPistol { AmmoMax = 11f, Rpm = 111 });
            writer.Add(new TestColdInfo { SelfId = writer.Id.Value, Tier = tier });
        }
    }

    /// <summary>A node only in the cold base: a row with one bit and a hole where the second would be.</summary>
    public sealed class TestColdOnlyNodeSo : BlobchegNodeSo
    {
        public int tier = 9;

        public override Type[] OutTypes => new[] { typeof(ITestColdData) };

        public override void Write(ref BlobchegNodeWriter writer)
            => writer.Add(new TestColdInfo { SelfId = writer.Id.Value, Tier = tier });
    }

    /// <summary>The files and manifests the rebuild lays down because of the test domains.</summary>
    public static class BlobchegTestArtifacts
    {
        static readonly string[] Names =
        {
            "ITestCombatData", "ITestColdData", "ITestBootData", "TestGameRouter",
            "ITestGridData", "TestFixedRouter",
        };

        public static void Wipe()
        {
            foreach (var name in Names)
            {
                var file = Path.Combine(BlobchegBuild.OutputDirectory, BlobchegNaming.FileName(name));
                if (File.Exists(file))
                    File.Delete(file);
            }

            AssetDatabase.Refresh();
        }
    }

    /// <summary>
    /// The end-to-end path of a router: nodes in the editor → the rebuild → the router file → the id
    /// carrier → a lookup of the offsets in every base at once.
    /// </summary>
    [TestFixture(BlobchegTestMode.Editor)]
    [TestFixture(BlobchegTestMode.AsInPlayer)]
    public sealed class BlobchegRouterPipelineTests
    {
        readonly BlobchegTestMode _mode;

        public BlobchegRouterPipelineTests(BlobchegTestMode mode) => _mode = mode;

        string _folder;
        TestModuleNodeSo _module;
        TestColdOnlyNodeSo _cold;

        [SetUp]
        public void SetUp()
        {
            BlobchegTestModes.Enter(_mode);
            // A folder of its own per test: asset deletion is deferred, and a reused name swallows an
            // asset created in a folder that has not been deleted yet.
            var name = "BlobchegRouterTemp_" + Guid.NewGuid().ToString("N");
            _folder = "Assets/" + name;
            AssetDatabase.CreateFolder("Assets", name);

            _module = Create<TestModuleNodeSo>("Module");
            _cold = Create<TestColdOnlyNodeSo>("ColdOnly");
            AssetDatabase.SaveAssets();
        }

        [TearDown]
        public void TearDown()
        {
            BlobchegTestModes.Leave();
            AssetDatabase.DeleteAsset(_folder);
            BlobchegTestArtifacts.Wipe();
        }

        T Create<T>(string name) where T : ScriptableObject
        {
            var path = _folder + "/" + name + ".asset";
            AssetDatabase.CreateAsset(ScriptableObject.CreateInstance<T>(), path);

            var asset = AssetDatabase.LoadAssetAtPath<T>(path);
            Assert.That(asset, Is.Not.Null, $"asset '{path}' was not created — there is nothing further to check");
            return asset;
        }

        static BlobchegId IdOf(BlobchegNodeSo node)
        {
            var carrier = BlobchegBuild.IdsOf(node).Single();
            Assert.That(carrier.RouterName, Is.EqualTo(TestGameRouter.RouterName));
            return new BlobchegIdRef<TestGameRouter>(carrier).Id;
        }

        static TestGameRouter LoadRouter()
        {
            var path = Path.Combine(BlobchegBuild.OutputDirectory, TestGameRouter.FileName);
            Assert.That(File.Exists(path), Is.True, "the router file must land in the output folder");
            return new TestGameRouter(BlobchegBuffer.From(File.ReadAllBytes(path), Allocator.Persistent));
        }

        static TestColdDb LoadCold()
            => new TestColdDb(BlobchegBuffer.From(
                File.ReadAllBytes(Path.Combine(BlobchegBuild.OutputDirectory, TestColdDb.FileName)), Allocator.Persistent));

        static TestCombatDb LoadCombat()
            => new TestCombatDb(BlobchegBuffer.From(
                File.ReadAllBytes(Path.Combine(BlobchegBuild.OutputDirectory, TestCombatDb.FileName)), Allocator.Persistent));

        [Test]
        public void The_codegen_and_the_registry_agree_on_the_bit_numbering()
        {
            Assert.That(TestGameRouter.DomainCount, Is.EqualTo(2));
            Assert.That(TestGameRouter.RouterName, Is.EqualTo("TestGameRouter"));
            Assert.That(BlobchegRouters.LayoutHashOf(typeof(TestGameRouter)), Is.EqualTo(TestGameRouter.LayoutHash));
            Assert.DoesNotThrow(() => BlobchegRouters.RequireCodeGenAgrees(typeof(TestGameRouter)));

            var domains = BlobchegRouters.DomainsOf(typeof(TestGameRouter));
            CollectionAssert.AreEqual(new[] { typeof(ITestColdData), typeof(ITestCombatData) }, domains,
                "the bits are numbered by the domains in ordinal FullName order");
        }

        [Test]
        public void One_id_gets_the_records_of_both_bases()
        {
            _module.tier = 42;
            EditorUtility.SetDirty(_module);
            AssetDatabase.SaveAssetIfDirty(_module);

            var report = BlobchegBuild.RebuildAll();
            Assert.That(report.Routers, Is.GreaterThanOrEqualTo(1));

            var id = IdOf(_module);
            var router = LoadRouter();
            var cold = LoadCold();
            var combat = LoadCombat();

            try
            {
                var row = router.Get(id);
                Assert.That(row.HasCold, Is.True);
                Assert.That(row.HasCombat, Is.True);

                ref readonly var record = ref cold.Read<TestColdInfo>(row.cold);
                Assert.That(record.Tier, Is.EqualTo(42));
                Assert.That(record.SelfId, Is.EqualTo(id.Value), "the node put its own id into the record — it is known before the write");

                ref readonly var pistol = ref combat.Read<TestPistol>(row.combat);
                Assert.That(pistol.Rpm, Is.EqualTo(111));

                Assert.That(router.GetCold(id), Is.EqualTo(row.cold), "the short path and the row give the same thing");
                Assert.That(router.TryGetCombat(id, out var offset), Is.True);
                Assert.That(offset, Is.EqualTo(row.combat));
            }
            finally
            {
                router.Dispose();
                cold.Dispose();
                combat.Dispose();
            }
        }

        [Test]
        public void A_node_outside_a_base_throws_while_Try_answers_false()
        {
            BlobchegBuild.RebuildAll();

            var id = IdOf(_cold);
            var router = LoadRouter();
            try
            {
                var row = router.Get(id);
                Assert.That(row.HasCold, Is.True);
                Assert.That(row.HasCombat, Is.False, "this node never wrote into the combat base");

                Assert.Throws<InvalidOperationException>(() => _ = row.combat);
                Assert.Throws<InvalidOperationException>(() => router.GetCombat(id));

                Assert.That(router.TryGetCombat(id, out _), Is.False);
                Assert.That(router.HasCombat(id), Is.False);
                Assert.That(router.HasCold(id), Is.True);
            }
            finally
            {
                router.Dispose();
            }
        }

        [Test]
        public void An_unknown_id_throws()
        {
            BlobchegBuild.RebuildAll();

            var router = LoadRouter();
            try
            {
                var beyond = BlobchegId.In(TestGameRouter.RouterName, (uint)router.Count);
                Assert.Throws<InvalidOperationException>(() => router.Get(beyond));
                Assert.Throws<InvalidOperationException>(() => router.Get(BlobchegId.None));
                Assert.That(router.TryGet(beyond, out _), Is.False);

                // A tag deliberately not of this router: the name of a foreign router could share a tag.
                var alienTag = (byte)(BlobchegNaming.TagOf(TestGameRouter.RouterName) % 255 + 1);
                var alien = BlobchegId.Make(alienTag, 0);
                Assert.Throws<InvalidOperationException>(() => router.Get(alien),
                    "row zero exists in this router, but the id was not handed out by it");
                Assert.That(router.TryGet(alien, out _), Is.False);
            }
            finally
            {
                router.Dispose();
            }
        }

        [Test]
        public void An_id_does_not_move_when_a_value_is_edited()
        {
            BlobchegBuild.RebuildAll();
            var before = IdOf(_module);

            _module.tier = 7;
            EditorUtility.SetDirty(_module);
            AssetDatabase.SaveAssetIfDirty(_module);
            BlobchegBuild.RebuildAll();

            Assert.That(IdOf(_module), Is.EqualTo(before));

            var cold = LoadCold();
            var router = LoadRouter();
            try
            {
                Assert.That(cold.Read<TestColdInfo>(router.Get(before).cold).Tier, Is.EqualTo(7),
                    "the value is obliged to change while it does");
            }
            finally
            {
                cold.Dispose();
                router.Dispose();
            }
        }

        [Test]
        public void A_player_reads_the_id_off_the_carrier_without_the_editor_table()
        {
            BlobchegBuild.RebuildAll();

            var inEditor = IdOf(_module);
            var was = BlobchegStamps.Source;
            BlobchegStamps.Source = null;
            try
            {
                Assert.That(IdOf(_module), Is.EqualTo(inEditor), "a player has no stamp table, the carrier alone names the id");
            }
            finally
            {
                BlobchegStamps.Source = was;
            }
        }

        [Test]
        public void The_row_of_an_id_lies_in_the_node_asset()
        {
            BlobchegBuild.RebuildAll();

            // Nothing local decides an id: a fresh checkout reads the row from the file git carries.
            var text = File.ReadAllText(AssetDatabase.GetAssetPath(_module));
            Assert.That(text, Does.Contain("row: " + IdOf(_module).Index));
        }

        [Test]
        public void A_duplicated_node_takes_no_row_from_the_original()
        {
            BlobchegBuild.RebuildAll();
            var original = IdOf(_module);

            // What Ctrl+D leaves behind: another node carrying a copy of the carrier, row and owner included.
            var copy = Create<TestModuleNodeSo>("ModuleCopy");
            var carrier = UnityEngine.Object.Instantiate(BlobchegBuild.IdsOf(_module).Single());
            carrier.name = BlobchegBuild.IdsOf(_module).Single().name;
            AssetDatabase.AddObjectToAsset(carrier, copy);
            AssetDatabase.SaveAssets();
            BlobchegBuild.RebuildAll();

            Assert.That(IdOf(_module), Is.EqualTo(original), "the original keeps its id against its own copy");
            Assert.That(IdOf(copy), Is.Not.EqualTo(original));
            Assert.That(IdOf(copy).Index, Is.GreaterThan(Math.Max(original.Index, IdOf(_cold).Index)));
        }

        [Test]
        public void A_new_node_moves_no_foreign_id_and_sits_at_the_tail()
        {
            BlobchegBuild.RebuildAll();

            var module = IdOf(_module);
            var cold = IdOf(_cold);

            // The GUID of a new node is random: in GUID order it could have landed before the others.
            var newcomer = Create<TestColdOnlyNodeSo>("Newcomer");
            AssetDatabase.SaveAssets();
            BlobchegBuild.RebuildAll();

            Assert.That(IdOf(_module), Is.EqualTo(module), "the neighbour's id is obliged to outlive the appearance of a new node");
            Assert.That(IdOf(_cold), Is.EqualTo(cold));
            Assert.That(IdOf(newcomer).Index, Is.GreaterThan(Math.Max(module.Index, cold.Index)),
                "a newcomer takes a row past every held one");

            var router = LoadRouter();
            try
            {
                Assert.That(router.Get(IdOf(newcomer)).HasCold, Is.True);
                Assert.That(router.Get(module).HasCold, Is.True, "the neighbour still reads by its old id");
            }
            finally
            {
                router.Dispose();
            }
        }

        [Test]
        public void A_deleted_node_leaves_a_hole_and_a_foreign_id_stays()
        {
            BlobchegBuild.RebuildAll();

            // Only the one whose id is not the last leaves a hole, and they are handed out by GUID —
            // so which one to kill is decided by a measurement and not by the creation order in the test.
            var first = IdOf(_module).Index < IdOf(_cold).Index;
            var victim = first ? (BlobchegNodeSo)_module : _cold;
            var survivor = first ? (BlobchegNodeSo)_cold : _module;

            var keep = IdOf(survivor);
            var killed = IdOf(victim);
            Assert.That(killed.Index, Is.LessThan(keep.Index));

            var rows = LoadRouterRowCount();

            AssetDatabase.DeleteAsset(AssetDatabase.GetAssetPath(victim));
            BlobchegBuild.RebuildAll();

            Assert.That(IdOf(survivor), Is.EqualTo(keep), "a foreign id does not slide down after a deleted one");
            Assert.That(LoadRouterRowCount(), Is.EqualTo(rows),
                "the row of a deleted node stays a hole: pulling the next one in means shifting its id");

            var router = LoadRouter();
            try
            {
                Assert.That(router.Get(keep).HasCold, Is.True, "the surviving node reads by its id the way it always did");
                Assert.That(router.Get(killed).HasCold, Is.False, "the hole is empty and does not point at a neighbour");
            }
            finally
            {
                router.Dispose();
            }
        }

        static int LoadRouterRowCount()
        {
            var router = LoadRouter();
            try
            {
                return (int)router.Count;
            }
            finally
            {
                router.Dispose();
            }
        }

        [Test]
        public void A_rebuild_after_a_deletion_agrees_with_itself()
        {
            BlobchegBuild.RebuildAll();

            var first = IdOf(_module).Index < IdOf(_cold).Index;
            var victim = first ? (BlobchegNodeSo)_module : _cold;
            var survivor = first ? (BlobchegNodeSo)_cold : _module;
            var kept = IdOf(survivor);

            AssetDatabase.DeleteAsset(AssetDatabase.GetAssetPath(victim));
            BlobchegBuild.RebuildAll();

            Assert.That(IdOf(survivor), Is.EqualTo(kept), "the survivor reads by the id it always had");

            var router = LoadRouter();
            try
            {
                Assert.That(router.Get(kept).HasCold, Is.True);
            }
            finally
            {
                router.Dispose();
            }

            Assert.That(BlobchegBuild.RebuildFull().Changed, Is.False,
                "and the rebuild agrees with itself");
        }

        [Test]
        public void A_rebuild_with_a_router_is_idempotent()
        {
            BlobchegBuild.RebuildAll();

            var again = BlobchegBuild.RebuildAll();
            Assert.That(again.Changed, Is.False,
                "nothing changed — neither the router file nor a single id carrier must be touched");
        }

        [Test]
        public void An_id_field_rejects_emptiness_and_a_foreign_router()
        {
            BlobchegBuild.RebuildAll();

            var empty = new BlobchegIdRef<TestGameRouter>(null);
            Assert.That(empty.IsSet, Is.False);
            Assert.Throws<InvalidOperationException>(() => _ = empty.Id);

            var alien = ScriptableObject.CreateInstance<BlobchegIdSo>();
            try
            {
                alien.name = "Alien";
                var thrown = Assert.Throws<InvalidOperationException>(
                    () => _ = new BlobchegIdRef<TestGameRouter>(alien).Id);

                StringAssert.Contains("TestGameRouter", thrown.Message);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(alien);
            }
        }

        [Test]
        public void The_picker_shows_only_the_nodes_of_its_own_router()
        {
            BlobchegBuild.RebuildAll();

            var mine = BlobchegIdCatalog.Candidates(TestGameRouter.RouterName);
            CollectionAssert.Contains(mine, BlobchegBuild.IdsOf(_module).Single());
            CollectionAssert.Contains(mine, BlobchegBuild.IdsOf(_cold).Single());

            Assert.That(BlobchegIdCatalog.Candidates("ForeignRouter"), Is.Empty);
            Assert.That(BlobchegIdCatalog.RouterNameOf(typeof(TestGameRouter)), Is.EqualTo("TestGameRouter"));
        }

        [Test]
        public void The_router_manifest_holds_the_file_hash_and_the_id_order()
        {
            var report = BlobchegBuild.RebuildAll();

            var manifest = BlobchegManifests.Of("TestGameRouter");

            Assert.That(manifest, Is.Not.Null);
            Assert.That(manifest.IsRouter, Is.True,
                $"report: {report}; file: {BlobchegManifests.FilePath}; " +
                $"domainName: '{manifest.domainName}'; recordCount: {manifest.recordCount}; " +
                $"hash: {manifest.ContentHash:X16}; nodes: {manifest.NodeCount}");

            var file = File.ReadAllBytes(Path.Combine(BlobchegBuild.OutputDirectory, TestGameRouter.FileName));
            Assert.That(manifest.ContentHash, Is.EqualTo(BitConverter.ToUInt64(file, 16)));

            // The nodes lie in the manifest in id order — that is the "id → node" table for the eye.
            for (var i = 0; i < manifest.NodeCount; i++)
            {
                var carrier = BlobchegBuild.IdsOf(manifest.NodeAt(i)).Single();
                Assert.That(carrier.Id.Index, Is.EqualTo((uint)i));
            }
        }
    }
}

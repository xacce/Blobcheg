using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Blobcheg.Authoring;
using NUnit.Framework;
using Unity.Collections;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blobcheg.Tests
{
    /// <summary>The domain of the deterministic router. Its own, so as not to mix with the ordinary one.</summary>
    public interface ITestGridData
    {
    }

    public struct TestGridInfo : ITestGridData
    {
        /// <summary>Its own id, put by the node into the record: it is known before the write here too.</summary>
        public uint SelfId;

        public int Tier;
    }

    /// <summary>The member name is <c>grid</c> and not <c>fixed</c>: the codegen makes a row field out of the member name.</summary>
    [Blobcheg(typeof(ITestGridData), "grid", Router = typeof(TestFixedRouter))]
    public partial struct TestGridDb
    {
    }

    /// <summary>The router whose row numbers are declared by the nodes.</summary>
    [BlobchegRouter(FixedIndex = true)]
    public partial struct TestFixedRouter
    {
    }

    /// <summary>A node that declares its number with a field.</summary>
    public sealed class TestFixedNodeSo : BlobchegNodeSo, IBlobchegIndexed
    {
        public uint index;
        public int tier = 1;

        public uint Index => index;

        public override Type[] OutTypes => new[] { typeof(ITestGridData) };

        public override void Write(ref BlobchegNodeWriter writer)
            => writer.Add(new TestGridInfo { SelfId = writer.Id.Value, Tier = tier });
    }

    /// <summary>A node of the same domain but without the interface — the refusal is checked on it.</summary>
    public sealed class TestBlindNodeSo : BlobchegNodeSo
    {
        public override Type[] OutTypes => new[] { typeof(ITestGridData) };

        public override void Write(ref BlobchegNodeWriter writer)
            => writer.Add(new TestGridInfo { SelfId = writer.Id.Value, Tier = 0 });
    }

    /// <summary>
    /// A deterministic router: the row number is declared by the node, the rebuild only collects and
    /// checks it. The id carrier is derived and not the source of truth.
    /// </summary>
    public sealed class BlobchegFixedIndexTests
    {
        string _folder;

        [SetUp]
        public void SetUp()
        {
            // A folder of its own per test: asset deletion is deferred, and a reused name swallows an
            // asset created in a folder that has not been deleted yet.
            var name = "BlobchegFixedTemp_" + Guid.NewGuid().ToString("N");
            _folder = "Assets/" + name;
            AssetDatabase.CreateFolder("Assets", name);
        }

        [TearDown]
        public void TearDown()
        {
            AssetDatabase.DeleteAsset(_folder);
            BlobchegTestArtifacts.Wipe();
        }

        TestFixedNodeSo Node(string name, uint index)
        {
            var path = _folder + "/" + name + ".asset";
            var created = ScriptableObject.CreateInstance<TestFixedNodeSo>();
            created.index = index;
            AssetDatabase.CreateAsset(created, path);

            var asset = AssetDatabase.LoadAssetAtPath<TestFixedNodeSo>(path);
            Assert.That(asset, Is.Not.Null, $"asset '{path}' was not created — there is nothing further to check");
            return asset;
        }

        static BlobchegId IdOf(BlobchegNodeSo node)
        {
            var carrier = BlobchegBuild.IdsOf(node)
                .Single(c => c.RouterName == TestFixedRouter.RouterName);

            return new BlobchegIdRef<TestFixedRouter>(carrier).Id;
        }

        static TestFixedRouter LoadRouter()
        {
            var path = Path.Combine(BlobchegBuild.OutputDirectory, TestFixedRouter.FileName);
            Assert.That(File.Exists(path), Is.True, "the router file must land in StreamingAssets");
            return new TestFixedRouter(BlobchegBuffer.From(File.ReadAllBytes(path), Allocator.Persistent));
        }

        static uint OffsetOf(BlobchegNodeSo node)
            => BlobchegBuild.RefsOf(node).Single(r => r.DomainName == "ITestGridData").offset;

        static TestGridDb LoadGrid()
            => new TestGridDb(BlobchegBuffer.From(
                File.ReadAllBytes(Path.Combine(BlobchegBuild.OutputDirectory, TestGridDb.FileName)),
                Allocator.Persistent));

        [Test]
        public void The_registry_knows_which_router_is_deterministic()
        {
            Assert.That(BlobchegRouters.IsFixed(typeof(TestFixedRouter)), Is.True);
            Assert.That(BlobchegRouters.IsFixed(typeof(TestGameRouter)), Is.False);
            Assert.DoesNotThrow(() => BlobchegRouters.RequireCodeGenAgrees(typeof(TestFixedRouter)),
                "the codegen does not read the arguments of [BlobchegRouter] — LayoutHash does not depend on the flag");
        }

        [Test]
        public void A_declared_number_becomes_the_row_of_the_id()
        {
            var third = Node("Third", 3);
            var seventh = Node("Seventh", 7);
            AssetDatabase.SaveAssets();

            BlobchegBuild.RebuildAll();

            var tag = BlobchegNaming.TagOf(TestFixedRouter.RouterName);

            Assert.That(IdOf(third).Index, Is.EqualTo(3u));
            Assert.That(IdOf(seventh).Index, Is.EqualTo(7u));
            Assert.That(IdOf(third).Tag, Is.EqualTo(tag), "the tag stays the router's — nobody declares it");

            var router = LoadRouter();
            var grid = LoadGrid();
            try
            {
                Assert.That(router.Count, Is.EqualTo(8u), "rows up to and including the last declared number");
                Assert.That(grid.Read<TestGridInfo>(router.Get(IdOf(seventh)).grid).SelfId,
                    Is.EqualTo(IdOf(seventh).Value), "the node put the declared id into the record");
            }
            finally
            {
                router.Dispose();
                grid.Dispose();
            }
        }

        [Test]
        public void Sparse_numbers_give_empty_rows()
        {
            Node("Zero", 0);
            Node("Far", 1000);
            AssetDatabase.SaveAssets();

            BlobchegBuild.RebuildAll();

            var router = LoadRouter();
            try
            {
                Assert.That(router.Count, Is.EqualTo(1001u), "the gap between the families is empty rows");

                var hole = BlobchegId.In(TestFixedRouter.RouterName, 500);
                Assert.That(router.Get(hole).HasGrid, Is.False, "the row is there but empty");
                Assert.That(router.TryGetGrid(hole, out _), Is.False);
            }
            finally
            {
                router.Dispose();
            }
        }

        [Test]
        public void Wiping_the_carriers_brings_back_the_same_ids()
        {
            var node = Node("Stable", 12);
            AssetDatabase.SaveAssets();
            BlobchegBuild.RebuildAll();

            var before = IdOf(node);
            Assert.That(before.Index, Is.EqualTo(12u),
                "the number is declared: handing them out would seat the only node in row zero");

            foreach (var carrier in BlobchegBuild.IdsOf(node).ToList())
            {
                AssetDatabase.RemoveObjectFromAsset(carrier);
                UnityEngine.Object.DestroyImmediate(carrier, true);
            }

            EditorUtility.SetDirty(node);
            AssetDatabase.SaveAssets();

            BlobchegBuild.RebuildFull();

            Assert.That(IdOf(node), Is.EqualTo(before),
                "the carrier is derived: the journal was wiped while the number is declared by the node");
        }

        [Test]
        public void The_carrier_is_not_asked_and_the_move_is_counted_and_logged()
        {
            var node = Node("Moved", 4);
            AssetDatabase.SaveAssets();
            BlobchegBuild.RebuildAll();

            // The carrier was swapped by hand: that is what a router that handed out numbers before the
            // flag was switched on looks like.
            var carrier = BlobchegBuild.IdsOf(node).Single(c => c.RouterName == TestFixedRouter.RouterName);
            carrier.id = BlobchegId.In(TestFixedRouter.RouterName, 100).Value;
            EditorUtility.SetDirty(carrier);
            AssetDatabase.SaveAssets();

            LogAssert.Expect(LogType.Log, new Regex("Moved.*100.*4"));

            var report = BlobchegBuild.RebuildFull();

            Assert.That(IdOf(node).Index, Is.EqualTo(4u), "the declaration is stronger than the journal");
            Assert.That(report.MovedIds, Is.EqualTo(1), "the move is obliged to be counted, not to happen silently");
        }

        [Test]
        public void A_compaction_does_not_move_declared_rows_but_re_packs_the_offsets()
        {
            var head = Node("Head", 0);
            var tail = Node("Tail", 9);
            AssetDatabase.SaveAssets();
            BlobchegBuild.RebuildAll();

            Assert.That(IdOf(tail).Index, Is.EqualTo(9u));

            // Only a record lying ahead of its neighbour leaves a hole in the base, and the order of the
            // records is decided by BuildOrder — so which one to delete is decided by a measurement and
            // not by the creation order in the test.
            var earlier = OffsetOf(head) < OffsetOf(tail);
            var victim = earlier ? (BlobchegNodeSo)head : tail;
            var survivor = earlier ? (BlobchegNodeSo)tail : head;
            var keep = IdOf(survivor);

            AssetDatabase.DeleteAsset(AssetDatabase.GetAssetPath(victim));
            BlobchegBuild.RebuildAll();

            var offsetBefore = OffsetOf(survivor);
            Assert.That(offsetBefore, Is.GreaterThan(0u), "a hole from the deleted record is left ahead");

            BlobchegBuild.Compact();

            Assert.That(IdOf(survivor), Is.EqualTo(keep),
                "a compaction does not ask the carriers of this router — and has nothing to move");

            var router = LoadRouter();
            try
            {
                Assert.That(router.Count, Is.EqualTo(keep.Index + 1),
                    "the rows are declared, and the holes in them are a declaration too");
            }
            finally
            {
                router.Dispose();
            }

            Assert.That(OffsetOf(survivor), Is.LessThan(offsetBefore),
                "while the compaction re-packed the offsets, as it always does");
        }

        [Test]
        public void Two_nodes_on_one_number_throw()
        {
            var first = Node("Alpha", 5);
            var second = Node("Beta", 5);
            AssetDatabase.SaveAssets();

            var error = Assert.Throws<InvalidOperationException>(() => BlobchegBuild.RebuildAll());
            Assert.That(error.Message, Does.Contain("Alpha").And.Contain("Beta").And.Contain("5"),
                "the text is obliged to carry both names and the number — the fix goes into one of the nodes");

            AssetDatabase.DeleteAsset(AssetDatabase.GetAssetPath(second));
            Assert.DoesNotThrow(() => BlobchegBuild.RebuildAll(), "the conflict is gone — the rebuild runs again");
            Assert.That(IdOf(first).Index, Is.EqualTo(5u));
        }

        [Test]
        public void A_node_without_the_interface_throws()
        {
            var blind = ScriptableObject.CreateInstance<TestBlindNodeSo>();
            AssetDatabase.CreateAsset(blind, _folder + "/Blind.asset");
            AssetDatabase.SaveAssets();

            var error = Assert.Throws<InvalidOperationException>(() => BlobchegBuild.RebuildAll());
            Assert.That(error.Message, Does.Contain("Blind").And.Contain(TestFixedRouter.RouterName)
                .And.Contain("IBlobchegIndexed"));

            AssetDatabase.DeleteAsset(_folder + "/Blind.asset");
            Assert.DoesNotThrow(() => BlobchegBuild.RebuildAll());
        }

        [Test]
        public void A_number_past_the_ceiling_throws()
        {
            var node = Node("Beyond", BlobchegId.MaxIndex + 1);
            AssetDatabase.SaveAssets();

            var error = Assert.Throws<InvalidOperationException>(() => BlobchegBuild.RebuildAll());
            Assert.That(error.Message, Does.Contain("Beyond").And.Contain(BlobchegId.MaxIndex.ToString()));

            node.index = 1;
            EditorUtility.SetDirty(node);
            AssetDatabase.SaveAssets();
            Assert.DoesNotThrow(() => BlobchegBuild.RebuildAll());
        }
    }
}

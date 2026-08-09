using System;
using System.IO;
using System.Linq;
using Blobcheg.Authoring;
using NUnit.Framework;
using Unity.Collections;
using UnityEditor;
using UnityEngine;

namespace Blobcheg.HashTests
{
    /// <summary>
    /// The end-to-end path of the table: node name → key → file → lookup. And the main property the
    /// whole thing exists for: the addresses move, the hash stays.
    /// </summary>
    public sealed class BlobchegHashesTests
    {
        static readonly string[] Artifacts =
        {
            "ITestHashHot", "ITestHashCold", "TestHashRouter", "TestHashRouterHashes",
        };

        string _folder;
        TestHashNodeSo _gun;
        TestHashNodeSo _twin;
        TestHashColdOnlyNodeSo _cold;

        [SetUp]
        public void SetUp()
        {
            var name = "BlobchegHashTemp_" + Guid.NewGuid().ToString("N");
            _folder = "Assets/" + name;
            AssetDatabase.CreateFolder("Assets", name);

            _gun = Create<TestHashNodeSo>("Gun");
            _twin = Create<TestHashNodeSo>("Twin");
            _cold = Create<TestHashColdOnlyNodeSo>("ColdOnly");

            _gun.twin = _twin;
            EditorUtility.SetDirty(_gun);
            AssetDatabase.SaveAssets();
        }

        [TearDown]
        public void TearDown()
        {
            AssetDatabase.DeleteAsset(_folder);

            foreach (var artifact in Artifacts)
            {
                AssetDatabase.DeleteAsset(BlobchegBuild.ManifestFolder + "/" + artifact + ".asset");

                var file = Path.Combine(BlobchegBuild.OutputDirectory, BlobchegNaming.FileName(artifact));
                if (File.Exists(file))
                    File.Delete(file);
            }

            AssetDatabase.Refresh();
        }

        T Create<T>(string name) where T : ScriptableObject
        {
            var path = _folder + "/" + name + ".asset";
            AssetDatabase.CreateAsset(ScriptableObject.CreateInstance<T>(), path);

            var asset = AssetDatabase.LoadAssetAtPath<T>(path);
            Assert.That(asset, Is.Not.Null, $"asset '{path}' was not created — there is nothing further to check");
            return asset;
        }

        static void Rename(BlobchegNodeSo node, string name)
        {
            var serialized = new SerializedObject(node);
            serialized.FindProperty("blobchegName").stringValue = name;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(node);
        }

        static string PathOf(string identity)
            => Path.Combine(BlobchegBuild.OutputDirectory, BlobchegNaming.FileName(identity));

        static BlobchegBuffer Read(string identity)
        {
            var path = PathOf(identity);
            Assert.That(File.Exists(path), Is.True, $"file '{path}' must land in StreamingAssets");
            return BlobchegBuffer.From(File.ReadAllBytes(path), Allocator.Persistent);
        }

        static TestHashTable LoadTable() => new TestHashTable(Read(TestHashTable.FileIdentity));

        static TestHashRouter LoadRouter() => new TestHashRouter(Read(TestHashRouter.RouterName));

        static TestHashHotDb LoadHot() => new TestHashHotDb(Read(TestHashHotDb.DomainName));

        static TestHashColdDb LoadCold() => new TestHashColdDb(Read(TestHashColdDb.DomainName));

        static BlobchegId IdOf(BlobchegNodeSo node)
        {
            var carrier = BlobchegBuild.IdsOf(node).Single(c => c.RouterName == TestHashRouter.RouterName);
            return new BlobchegId(carrier.id);
        }

        static uint OffsetOf(BlobchegNodeSo node, string domainName)
            => BlobchegBuild.RefsOf(node).Single(r => r.DomainName == domainName).offset;

        [Test]
        public void The_key_is_computed_from_the_router_name_and_the_node_name()
        {
            var direct = BlobchegHashKey.Of("TestHashRouter", "ak74");
            Assert.That(BlobchegHashKey.Of<TestHashRouter>("ak74"), Is.EqualTo(direct),
                "the router name is taken from the type parameter, not written by hand");

            Assert.That(BlobchegHashKey.Of("TestHashRouter", "ak74m"), Is.Not.EqualTo(direct));
            Assert.That(BlobchegHashKey.Of("OtherRouter", "ak74"), Is.Not.EqualTo(direct),
                "the router is part of the key: without it one name in two routers would give one hash");

            Assert.That(direct, Is.Not.EqualTo(0ul), "zero is taken by \"not assigned\"");

            Assert.Throws<ArgumentException>(() => BlobchegHashKey.Of("TestHashRouter", ""));
            Assert.Throws<ArgumentException>(() => BlobchegHashKey.Of("", "ak74"));
        }

        [Test]
        public void An_empty_name_is_filled_once_with_the_asset_name()
        {
            Assert.That(_gun.BlobchegName, Is.Null.Or.Empty, "before the rebuild there is no name");

            var first = BlobchegBuild.RebuildAll();
            Assert.That(first.NamedNodes, Is.GreaterThanOrEqualTo(3), "the names were stamped in the first run");
            Assert.That(_gun.BlobchegName, Is.EqualTo("Gun"));
            Assert.That(_cold.BlobchegName, Is.EqualTo("ColdOnly"));

            var again = BlobchegBuild.RebuildAll();
            Assert.That(again.NamedNodes, Is.EqualTo(0), "the second run does not touch the names");
            Assert.That(again.Changed, Is.False, "a rebuild with a table is obliged to be idempotent");
        }

        [Test]
        public void A_hash_unfolds_into_an_id_and_back()
        {
            BlobchegBuild.RebuildAll();

            var table = LoadTable();
            try
            {
                Assert.That(table.Count, Is.EqualTo(3));
                Assert.That(table.Tag, Is.EqualTo(BlobchegNaming.TagOf(TestHashRouter.RouterName)));

                foreach (var node in new BlobchegNodeSo[] { _gun, _twin, _cold })
                {
                    var id = IdOf(node);
                    var hash = BlobchegHashKey.Of<TestHashRouter>(node.BlobchegName);

                    Assert.That(table.TryGetId(hash, out var found), Is.True, $"node '{node.name}' was not found by hash");
                    Assert.That(found, Is.EqualTo(id));
                    Assert.That(table.GetId(hash), Is.EqualTo(id));
                    Assert.That(table.HashOf(id), Is.EqualTo(hash), "the way back is obliged to give the same hash");
                }
            }
            finally
            {
                table.Dispose();
            }
        }

        [Test]
        public void An_unknown_hash_and_zero_are_not_found()
        {
            BlobchegBuild.RebuildAll();

            var table = LoadTable();
            try
            {
                Assert.That(table.TryGetId(BlobchegHashKey.Of<TestHashRouter>("no such node"), out _), Is.False);
                Assert.Throws<InvalidOperationException>(
                    () => table.GetId(BlobchegHashKey.Of<TestHashRouter>("no such node")));

                Assert.That(table.TryGetId(0, out _), Is.False,
                    "zero is an empty slot, not the first row");

                var alienTag = (byte)(table.Tag % 255 + 1);
                Assert.Throws<InvalidOperationException>(() => table.HashOf(BlobchegId.Make(alienTag, 0)),
                    "an id of a foreign router means nothing here");
            }
            finally
            {
                table.Dispose();
            }
        }

        [Test]
        public void The_hole_from_a_deleted_node_hands_out_zero()
        {
            BlobchegBuild.RebuildAll();

            // Only the one whose number is not the last leaves a hole; they are handed out by GUID, so
            // which one to kill is decided by a measurement.
            var victim = new BlobchegNodeSo[] { _gun, _twin, _cold }.OrderBy(n => IdOf(n).Index).First();
            var killed = IdOf(victim);

            AssetDatabase.DeleteAsset(AssetDatabase.GetAssetPath(victim));
            BlobchegBuild.RebuildAll();

            var table = LoadTable();
            try
            {
                Assert.That(table.HashOf(killed), Is.EqualTo(0ul), "the row is there but empty");
                Assert.That(table.TryGetId(BlobchegHashKey.Of<TestHashRouter>("Gun"), out _),
                    Is.EqualTo(victim != (BlobchegNodeSo)_gun), "a deleted node is no longer found by hash");
            }
            finally
            {
                table.Dispose();
            }
        }

        [Test]
        public void The_hash_by_record_address_agrees_with_the_hash_by_id()
        {
            BlobchegBuild.RebuildAll();

            var table = LoadTable();
            try
            {
                var hash = BlobchegHashKey.Of<TestHashRouter>(_gun.BlobchegName);

                Assert.That(table.HashOfHot(OffsetOf(_gun, "ITestHashHot")), Is.EqualTo(hash));
                Assert.That(table.HashOfCold(OffsetOf(_gun, "ITestHashCold")), Is.EqualTo(hash));

                // A node only in the cold base: its address is not in the hot lane at all.
                Assert.That(table.TryHashOfCold(OffsetOf(_cold, "ITestHashCold"), out var coldHash), Is.True);
                Assert.That(coldHash, Is.EqualTo(BlobchegHashKey.Of<TestHashRouter>(_cold.BlobchegName)));

                Assert.That(table.TryHashOfHot(7, out _), Is.False, "a foreign address is not an answer but a false");
                Assert.Throws<InvalidOperationException>(() => table.HashOfHot(7));
            }
            finally
            {
                table.Dispose();
            }
        }

        [Test]
        public void A_record_carries_its_own_hash_and_the_neighbours_hash()
        {
            BlobchegBuild.RebuildAll();

            var table = LoadTable();
            var hot = LoadHot();
            var router = LoadRouter();
            try
            {
                ref readonly var record = ref hot.Read<TestHashHotRecord>(router.Get(IdOf(_gun)).hot);

                Assert.That(record.Self, Is.EqualTo(BlobchegHashKey.Of<TestHashRouter>(_gun.BlobchegName)));
                Assert.That(table.GetId(record.Self), Is.EqualTo(IdOf(_gun)));
                Assert.That(table.GetId(record.Twin), Is.EqualTo(IdOf(_twin)),
                    "the neighbour's hash in the record unfolds into its row");
            }
            finally
            {
                table.Dispose();
                hot.Dispose();
                router.Dispose();
            }
        }

        [Test]
        public void A_compaction_moves_the_addresses_and_the_hash_stays()
        {
            BlobchegBuild.RebuildAll();

            var victim = new BlobchegNodeSo[] { _gun, _twin, _cold }.OrderBy(n => IdOf(n).Index).First();
            var survivor = victim == (BlobchegNodeSo)_gun ? _twin : _gun;

            var hash = BlobchegHashKey.Of<TestHashRouter>(survivor.BlobchegName);
            var wasId = IdOf(survivor);

            AssetDatabase.DeleteAsset(AssetDatabase.GetAssetPath(victim));
            BlobchegBuild.RebuildAll();
            BlobchegBuild.Compact();

            var nowId = IdOf(survivor);
            Assert.That(nowId, Is.Not.EqualTo(wasId), "the compaction is obliged to shift the row number — otherwise the test catches nothing");
            Assert.That(BlobchegHashKey.Of<TestHashRouter>(survivor.BlobchegName), Is.EqualTo(hash),
                "the hash does not depend on the compaction: it is computed from the name");

            var table = LoadTable();
            var hot = LoadHot();
            var router = LoadRouter();
            try
            {
                Assert.That(table.TryGetId(hash, out var found), Is.True, "the old hash is obliged to be found after a compaction");
                Assert.That(found, Is.EqualTo(nowId), "and to lead to the NEW row number");

                ref readonly var record = ref hot.Read<TestHashHotRecord>(router.Get(found).hot);
                Assert.That(record.Self, Is.EqualTo(hash));
            }
            finally
            {
                table.Dispose();
                hot.Dispose();
                router.Dispose();
            }
        }

        /// <summary>
        /// Renaming the asset right here is not possible: after a <c>RenameAsset</c> in batch mode the
        /// rebuild refuses to work until the editor finishes importing, and that is its own rule. The
        /// same thing is checked from the other side — the node name is separated from the asset name,
        /// and the hash is computed from the node name.
        /// </summary>
        [Test]
        public void The_hash_is_computed_from_the_node_name_and_not_from_the_asset_name()
        {
            BlobchegBuild.RebuildAll();

            var byAssetName = BlobchegHashKey.Of<TestHashRouter>("Gun");

            Rename(_gun, "ak74m");
            AssetDatabase.SaveAssets();
            BlobchegBuild.RebuildAll();

            Assert.That(_gun.name, Is.EqualTo("Gun"), "the asset name is untouched");

            var table = LoadTable();
            var hot = LoadHot();
            var router = LoadRouter();
            try
            {
                Assert.That(table.TryGetId(byAssetName, out _), Is.False,
                    "the asset name has nothing to do with the hash, and the previous node name is no longer found: " +
                    "there is no list of former names");

                var now = BlobchegHashKey.Of<TestHashRouter>("ak74m");
                Assert.That(table.TryGetId(now, out var found), Is.True);
                Assert.That(found, Is.EqualTo(IdOf(_gun)));

                ref readonly var record = ref hot.Read<TestHashHotRecord>(router.Get(found).hot);
                Assert.That(record.Self, Is.EqualTo(now), "the record was rebuilt with the new hash");
            }
            finally
            {
                table.Dispose();
                hot.Dispose();
                router.Dispose();
            }
        }

        [Test]
        public void Two_identical_names_fail_the_rebuild()
        {
            BlobchegBuild.RebuildAll();

            Rename(_twin, _gun.BlobchegName);
            AssetDatabase.SaveAssets();

            var thrown = Assert.Throws<InvalidOperationException>(() => BlobchegBuild.RebuildAll());
            StringAssert.Contains(_gun.BlobchegName, thrown.Message);
            StringAssert.Contains("TestHashRouter", thrown.Message);

            // Put the project back into a working state, otherwise the next rebuild in TearDown fails too.
            Rename(_twin, "Twin");
            AssetDatabase.SaveAssets();
            Assert.DoesNotThrow(() => BlobchegBuild.RebuildAll());
        }

        [Test]
        public void A_foreign_file_and_a_foreign_layout_do_not_load()
        {
            BlobchegBuild.RebuildAll();

            var alienName = Read(TestHashTable.FileIdentity);
            try
            {
                Assert.Throws<InvalidOperationException>(() => new BlobchegHashesBlob(
                    alienName, "ForeignTable", TestHashRouter.RouterName,
                    TestHashTable.DomainCount, TestHashTable.LayoutHash), "the identity of the file is obliged to agree");
            }
            finally
            {
                alienName.Dispose();
            }

            var alienLayout = Read(TestHashTable.FileIdentity);
            try
            {
                Assert.Throws<InvalidOperationException>(() => new BlobchegHashesBlob(
                    alienLayout, TestHashTable.FileIdentity, TestHashRouter.RouterName,
                    TestHashTable.DomainCount, TestHashTable.LayoutHash + 1), "the bit layout is obliged to agree");
            }
            finally
            {
                alienLayout.Dispose();
            }

            var router = Read(TestHashRouter.RouterName);
            try
            {
                Assert.Throws<InvalidOperationException>(() => new BlobchegHashesBlob(
                    router, TestHashTable.FileIdentity, TestHashRouter.RouterName,
                    TestHashTable.DomainCount, TestHashTable.LayoutHash),
                    "a router file does not load as a table");
            }
            finally
            {
                router.Dispose();
            }
        }

        [Test]
        public void The_table_manifest_holds_the_file_hash_and_the_row_order()
        {
            BlobchegBuild.RebuildAll();

            var manifest = AssetDatabase.LoadAssetAtPath<BlobchegDomainSo>(
                BlobchegBuild.ManifestFolder + "/" + TestHashTable.FileIdentity + ".asset");

            Assert.That(manifest, Is.Not.Null);
            Assert.That(manifest.kind, Is.EqualTo(BlobchegFileKind.Hashes));
            Assert.That(manifest.fileName, Is.EqualTo(TestHashTable.FileName));

            var file = File.ReadAllBytes(PathOf(TestHashTable.FileIdentity));
            Assert.That(manifest.ContentHash, Is.EqualTo(BitConverter.ToUInt64(file, 16)));

            for (var i = 0; i < manifest.nodes.Length; i++)
            {
                if (manifest.nodes[i] == null)
                    continue;

                Assert.That(IdOf(manifest.nodes[i]).Index, Is.EqualTo((uint)i),
                    "the nodes lie in the manifest in row order");
            }
        }

        [Test]
        public void The_hash_of_a_node_that_does_not_exist_throws()
            => Assert.Throws<ArgumentNullException>(() => ((BlobchegNodeSo)null).HashIn<TestHashRouter>());
    }
}

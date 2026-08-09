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
    /// <summary>The domain of the test. The marker interface is the base: one domain, one file.</summary>
    public interface ITestCombatData
    {
    }

    public struct TestPistol : ITestCombatData
    {
        public float AmmoMax;
        public int Rpm;
    }

    public struct TestArmor : ITestCombatData
    {
        public float Hp;
    }

    /// <summary>
    /// The declaration of a base. The whole body is added by the generator — if it did not run, the test
    /// does not build. The member name joins the router, and the router itself is named: the test
    /// assembly holds two of them.
    /// </summary>
    [Blobcheg(typeof(ITestCombatData), "combat", Router = typeof(TestGameRouter))]
    public partial struct TestCombatDb
    {
    }

    public sealed class TestPistolNodeSo : BlobchegNodeSo
    {
        public float ammoMax = 30f;
        public int rpm = 600;

        public override Type[] OutTypes => new[] { typeof(ITestCombatData) };

        public override void Write(ref BlobchegNodeWriter writer)
            => writer.Add(new TestPistol { AmmoMax = ammoMax, Rpm = rpm });
    }

    public sealed class TestArmorNodeSo : BlobchegNodeSo
    {
        public float hp = 100f;

        public override Type[] OutTypes => new[] { typeof(ITestCombatData) };

        public override void Write(ref BlobchegNodeWriter writer)
            => writer.Add(new TestArmor { Hp = hp });
    }

    public struct TestLootTable : ITestCombatData
    {
        public int Rolls;
        public BlobchegArray<float> Weights;
    }

    public sealed class TestLootNodeSo : BlobchegNodeSo
    {
        public int rolls = 2;
        public float[] weights = { 0.5f, 0.3f, 0.2f };

        public override Type[] OutTypes => new[] { typeof(ITestCombatData) };

        public override void Write(ref BlobchegNodeWriter writer)
        {
            var b = writer.Begin<TestLootTable>();
            b.Root.Rolls = rolls;

            var w = b.Allocate(ref b.Root.Weights, weights.Length);
            for (var i = 0; i < w.Length; i++)
                w[i] = weights[i];

            b.End();
        }
    }

    /// <summary>A broken node: a record with an array written as a struct literal. The rebuild is obliged to reject it.</summary>
    public sealed class TestLootLiteralNodeSo : BlobchegNodeSo
    {
        public override Type[] OutTypes => new[] { typeof(ITestCombatData) };

        public override void Write(ref BlobchegNodeWriter writer)
            => writer.Add(new TestLootTable { Rolls = 1 });
    }

    /// <summary>A broken node: it opened a builder and never called End.</summary>
    public sealed class TestLootUnclosedNodeSo : BlobchegNodeSo
    {
        public override Type[] OutTypes => new[] { typeof(ITestCombatData) };

        public override void Write(ref BlobchegNodeWriter writer)
        {
            var b = writer.Begin<TestLootTable>();
            b.Root.Rolls = 1;
        }
    }

    /// <summary>
    /// The end-to-end path: a node in the editor → the rebuild → the file → the ref asset → a read at an
    /// offset. There is no Save button on this path, so the rebuild is called directly — the same way the
    /// hooks call it.
    /// </summary>
    public sealed class BlobchegPipelineTests
    {
        // A folder of its own per test: asset deletion is deferred, and a reused name swallows an asset
        // created in a folder that has not been deleted yet. That gets caught not where it broke.
        string _folder;

        TestPistolNodeSo _pistol;
        TestArmorNodeSo _armor;

        [SetUp]
        public void SetUp()
        {
            var name = "BlobchegTestsTemp_" + Guid.NewGuid().ToString("N");
            _folder = "Assets/" + name;
            AssetDatabase.CreateFolder("Assets", name);

            _pistol = Create<TestPistolNodeSo>("Pistol");
            _armor = Create<TestArmorNodeSo>("Armor");
            AssetDatabase.SaveAssets();
        }

        [TearDown]
        public void TearDown()
        {
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

        [Test]
        public void Nodes_are_found_by_scanning_the_project()
        {
            var found = BlobchegBuild.FindNodes();
            CollectionAssert.Contains(found, _pistol);
            CollectionAssert.Contains(found, _armor);
        }

        static BlobchegRefSo RefOf(BlobchegNodeSo node)
            => BlobchegBuild.RefsOf(node).Single();

        [Test]
        public void The_rebuild_lays_down_the_file_the_ref_assets_and_reads_at_an_offset()
        {
            _pistol.ammoMax = 42f;
            _pistol.rpm = 900;
            EditorUtility.SetDirty(_pistol);

            var report = BlobchegBuild.RebuildAll();
            Assert.That(report.Records, Is.GreaterThanOrEqualTo(2));

            var file = Path.Combine(BlobchegBuild.OutputDirectory, TestCombatDb.FileName);
            Assert.That(File.Exists(file), Is.True, "the base file must land in StreamingAssets");

            var pistolRef = RefOf(_pistol);
            Assert.That(pistolRef.RecordType, Is.EqualTo(typeof(TestPistol).FullName));
            Assert.That(pistolRef.DomainName, Is.EqualTo("ITestCombatData"));

            var db = new TestCombatDb(BlobchegBuffer.From(File.ReadAllBytes(file), Allocator.Temp));
            try
            {
                ref readonly var pistol = ref db.Read<TestPistol>(pistolRef.offset);
                Assert.That(pistol.AmmoMax, Is.EqualTo(42f));
                Assert.That(pistol.Rpm, Is.EqualTo(900));

                ref readonly var armor = ref db.Read<TestArmor>(RefOf(_armor).offset);
                Assert.That(armor.Hp, Is.EqualTo(100f));
            }
            finally
            {
                db.Dispose();
            }
        }

        [Test]
        public void The_rebuild_is_idempotent()
        {
            BlobchegBuild.RebuildAll();

            var again = BlobchegBuild.RebuildAll();
            Assert.That(again.Changed, Is.False,
                "nothing changed — neither the file nor a single asset must be touched, otherwise everything gets rebaked. " +
                $"Report: {again}");
        }

        [Test]
        public void Editing_a_value_does_not_move_the_offset()
        {
            BlobchegBuild.RebuildAll();
            var before = RefOf(_pistol).offset;

            _pistol.ammoMax = 7f;
            EditorUtility.SetDirty(_pistol);
            BlobchegBuild.RebuildAll();

            Assert.That(RefOf(_pistol).offset, Is.EqualTo(before));

            var file = Path.Combine(BlobchegBuild.OutputDirectory, TestCombatDb.FileName);
            var db = new TestCombatDb(BlobchegBuffer.From(File.ReadAllBytes(file), Allocator.Temp));
            try
            {
                Assert.That(db.Read<TestPistol>(before).AmmoMax, Is.EqualTo(7f), "the value is obliged to change while it does");
            }
            finally
            {
                db.Dispose();
            }
        }

        static byte[] DomainFile()
            => File.ReadAllBytes(Path.Combine(BlobchegBuild.OutputDirectory, TestCombatDb.FileName));

        /// <summary>
        /// The main property of the cache: what was assembled from memory is obliged to match what was
        /// assembled from the assets. A full run that changed nothing is the proof of that match.
        /// </summary>
        [Test]
        public void An_incremental_rebuild_matches_a_full_one()
        {
            BlobchegBuild.RebuildAll();

            _pistol.ammoMax = 3f;
            EditorUtility.SetDirty(_pistol);
            BlobchegBuild.RebuildAll();

            var incremental = DomainFile();
            var full = BlobchegBuild.RebuildFull();

            Assert.That(full.Changed, Is.False,
                $"a full run after an incremental one is obliged to find no discrepancies. Report: {full}");
            CollectionAssert.AreEqual(incremental, DomainFile());
        }

        [Test]
        public void A_new_node_makes_it_into_an_incremental_rebuild()
        {
            BlobchegBuild.RebuildAll();

            var extra = Create<TestArmorNodeSo>("Extra");
            AssetDatabase.SaveAssets();
            BlobchegBuild.RebuildAll();

            Assert.That(BlobchegBuild.RefsOf(extra).Any(), Is.True, "a node created after the build is obliged to make it into it");

            var incremental = DomainFile();
            Assert.That(BlobchegBuild.RebuildFull().Changed, Is.False);
            CollectionAssert.AreEqual(incremental, DomainFile());
        }

        [Test]
        public void A_deleted_node_leaves_an_incremental_rebuild()
        {
            var extra = Create<TestArmorNodeSo>("Extra");
            AssetDatabase.SaveAssets();
            BlobchegBuild.RebuildAll();

            var records = BlobchegBuild.RebuildAll().Records;

            AssetDatabase.DeleteAsset(AssetDatabase.GetAssetPath(extra));
            var after = BlobchegBuild.RebuildAll();

            Assert.That(after.Records, Is.EqualTo(records - 1), "the record of a deleted node is obliged to leave the base");

            var incremental = DomainFile();
            Assert.That(BlobchegBuild.RebuildFull().Changed, Is.False);
            CollectionAssert.AreEqual(incremental, DomainFile());
        }

        [Test]
        public void A_typed_field_does_not_accept_a_foreign_record()
        {
            BlobchegBuild.RebuildAll();

            var field = new BlobchegRef<TestPistol>(RefOf(_armor));
            var thrown = Assert.Throws<InvalidOperationException>(() => _ = field.Offset);
            StringAssert.Contains("TestArmor", thrown.Message);

            Assert.That(new BlobchegRef<TestPistol>(RefOf(_pistol)).Offset, Is.EqualTo(RefOf(_pistol).offset));
        }

        [Test]
        public void The_picker_shows_only_records_of_its_own_type()
        {
            BlobchegBuild.RebuildAll();

            var pistols = BlobchegRefCatalog.Candidates(typeof(TestPistol));
            CollectionAssert.Contains(pistols, RefOf(_pistol));
            CollectionAssert.DoesNotContain(pistols, RefOf(_armor));

            var armors = BlobchegRefCatalog.Candidates(typeof(TestArmor));
            CollectionAssert.Contains(armors, RefOf(_armor));
            CollectionAssert.DoesNotContain(armors, RefOf(_pistol));

            var raw = BlobchegRefCatalog.Candidates(null);
            CollectionAssert.IsSupersetOf(raw, new[] { RefOf(_pistol), RefOf(_armor) });
        }

        [Test]
        public void The_catalogue_rejects_a_foreign_record()
        {
            BlobchegBuild.RebuildAll();

            Assert.That(BlobchegRefCatalog.Matches(RefOf(_armor), typeof(TestPistol)), Is.False);
            Assert.That(BlobchegRefCatalog.Matches(RefOf(_pistol), typeof(TestPistol)), Is.True);
            Assert.That(BlobchegRefCatalog.Matches(null, typeof(TestPistol)), Is.False);
        }

        [Test]
        public void An_empty_field_throws_instead_of_handing_out_zero()
        {
            var empty = new BlobchegRef<TestPistol>(null);
            Assert.That(empty.IsSet, Is.False);
            Assert.Throws<InvalidOperationException>(() => _ = empty.Offset);
        }

        [Test]
        public void A_node_with_an_array_is_written_and_read_through_the_rebuild()
        {
            var loot = Create<TestLootNodeSo>("Loot");
            AssetDatabase.SaveAssets();
            BlobchegBuild.RebuildAll();

            var file = Path.Combine(BlobchegBuild.OutputDirectory, TestCombatDb.FileName);
            var db = new TestCombatDb(BlobchegBuffer.From(File.ReadAllBytes(file), Allocator.Temp));
            try
            {
                ref readonly var table = ref db.Read<TestLootTable>(RefOf(loot).offset);
                Assert.That(table.Rolls, Is.EqualTo(2));
                Assert.That(table.Weights.Length, Is.EqualTo(3));
                Assert.That(table.Weights[0], Is.EqualTo(0.5f));
                Assert.That(table.Weights[1], Is.EqualTo(0.3f));
                Assert.That(table.Weights[2], Is.EqualTo(0.2f));
            }
            finally
            {
                db.Dispose();
            }
        }

        [Test]
        public void Editing_the_length_of_an_array_does_not_move_other_addresses()
        {
            var loot = Create<TestLootNodeSo>("Loot");
            AssetDatabase.SaveAssets();
            BlobchegBuild.RebuildAll();
            var pistolBefore = RefOf(_pistol).offset;
            var armorBefore = RefOf(_armor).offset;

            loot.weights = new[] { 0.3f, 0.25f, 0.2f, 0.15f, 0.06f, 0.04f };
            EditorUtility.SetDirty(loot);
            BlobchegBuild.RebuildAll();

            Assert.That(RefOf(_pistol).offset, Is.EqualTo(pistolBefore), "a grown array moves only its own record");
            Assert.That(RefOf(_armor).offset, Is.EqualTo(armorBefore));

            var file = Path.Combine(BlobchegBuild.OutputDirectory, TestCombatDb.FileName);
            var db = new TestCombatDb(BlobchegBuffer.From(File.ReadAllBytes(file), Allocator.Temp));
            try
            {
                Assert.That(db.Read<TestLootTable>(RefOf(loot).offset).Weights.Length, Is.EqualTo(6));
            }
            finally
            {
                db.Dispose();
            }
        }

        [Test]
        public void A_literal_with_an_array_is_rejected()
        {
            Create<TestLootLiteralNodeSo>("LootLiteral");
            AssetDatabase.SaveAssets();

            var thrown = Assert.Throws<InvalidOperationException>(() => BlobchegBuild.RebuildAll());
            StringAssert.Contains("Begin", thrown.Message, "the error is obliged to name the right form of the record");
        }

        [Test]
        public void Begin_without_End_fails_naming_the_node()
        {
            Create<TestLootUnclosedNodeSo>("LootUnclosed");
            AssetDatabase.SaveAssets();

            var thrown = Assert.Throws<InvalidOperationException>(() => BlobchegBuild.RebuildAll());
            StringAssert.Contains("LootUnclosed", thrown.Message);
            StringAssert.Contains("End", thrown.Message);
        }

        /// <summary>
        /// What the rebuild command in the menu exists for: files get lost past the assets
        /// (<c>git clean -X</c>, a fresh worktree), there are no dirty nodes while that happens, and the
        /// rebuild will not fire from an import or from PlayMode — there is simply nothing to call them
        /// with.
        /// </summary>
        [Test]
        public void A_wiped_file_is_brought_back_by_a_rebuild_by_hand()
        {
            BlobchegBuild.RebuildAll();

            var file = Path.Combine(BlobchegBuild.OutputDirectory, TestCombatDb.FileName);
            var were = File.ReadAllBytes(file);
            var seen = BlobchegFileVersions.Of(TestCombatDb.FileName);

            File.Delete(file);

            var report = BlobchegBuild.RebuildFull();

            Assert.That(File.Exists(file), Is.True, "the menu command stands on exactly this");
            Assert.That(File.ReadAllBytes(file), Is.EqualTo(were),
                "the bytes are the same: the layout is deterministic, losing the file does not move the addresses");
            Assert.That(report.ChangedFiles, Is.GreaterThanOrEqualTo(1));
            Assert.That(BlobchegFileVersions.Of(TestCombatDb.FileName), Is.GreaterThan(seen),
                "the live world learns about the restored file by its number and in no other way");
        }

        [Test]
        public void The_domain_manifest_holds_the_same_hash_as_the_file()
        {
            BlobchegBuild.RebuildAll();

            var manifest = AssetDatabase.LoadAssetAtPath<BlobchegDomainSo>(
                BlobchegBuild.ManifestFolder + "/ITestCombatData.asset");
            Assert.That(manifest, Is.Not.Null);

            var file = File.ReadAllBytes(Path.Combine(BlobchegBuild.OutputDirectory, TestCombatDb.FileName));
            var inHeader = BitConverter.ToUInt64(file, 16);
            Assert.That(manifest.ContentHash, Is.EqualTo(inHeader));
        }
    }
}

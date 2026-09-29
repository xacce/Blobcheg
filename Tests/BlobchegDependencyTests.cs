using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Blobcheg.Authoring;
using NUnit.Framework;
using Unity.Collections;
using UnityEditor;
using UnityEngine;

namespace Blobcheg.Tests
{
    public struct TestTurret : ITestCombatData
    {
        public float BarrelLength;
    }

    /// <summary>A foreign asset: not a node, just a ScriptableObject a node reads at write.</summary>
    public sealed class TestBarrelSo : ScriptableObject
    {
        public float length = 1f;
    }

    /// <summary>A node whose record is made of a foreign asset: the length lives in the barrel, not in the node.</summary>
    public sealed class TestTurretNodeSo : BlobchegNodeSo
    {
        public TestBarrelSo barrel;

        public override Type[] OutTypes => new[] { typeof(ITestCombatData) };

        public override void Write(ref BlobchegNodeWriter writer)
            => writer.Add(new TestTurret { BarrelLength = barrel != null ? barrel.length : 0f });
    }

    // A second step on the road to the value: the node reads the holder, the holder reads the barrel.
    public sealed class TestBarrelHolderSo : ScriptableObject
    {
        public TestBarrelSo barrel;
    }

    public sealed class TestHolderNodeSo : BlobchegNodeSo
    {
        public TestBarrelHolderSo holder;

        public override Type[] OutTypes => new[] { typeof(ITestCombatData) };

        public override void Write(ref BlobchegNodeWriter writer)
            => writer.Add(new TestTurret
            {
                BarrelLength = holder != null && holder.barrel != null ? holder.barrel.length : 0f,
            });
    }

    // A node that reads a file by path: no serialised reference points at it.
    public sealed class TestSidecarNodeSo : BlobchegNodeSo
    {
        public string sidecarPath;

        public override Type[] OutTypes => new[] { typeof(ITestCombatData) };

        public override void Write(ref BlobchegNodeWriter writer)
        {
            var text = !string.IsNullOrEmpty(sidecarPath) && File.Exists(sidecarPath)
                ? File.ReadAllText(sidecarPath)
                : "0";

            writer.Add(new TestTurret { BarrelLength = float.Parse(text, CultureInfo.InvariantCulture) });
        }

        public override void CollectExtraDependencies(List<string> paths)
        {
            if (!string.IsNullOrEmpty(sidecarPath))
                paths.Add(sidecarPath);
        }
    }

    // A node reads a foreign asset, the asset is edited, the node itself is never touched.
    [TestFixture(BlobchegTestMode.Editor)]
    [TestFixture(BlobchegTestMode.AsInPlayer)]
    public sealed class BlobchegDependencyTests
    {
        readonly BlobchegTestMode _mode;

        public BlobchegDependencyTests(BlobchegTestMode mode) => _mode = mode;

        string _folder;

        TestBarrelSo _barrel;
        TestTurretNodeSo _turret;

        [SetUp]
        public void SetUp()
        {
            BlobchegTestModes.Enter(_mode);
            var name = "BlobchegTestsTemp_" + Guid.NewGuid().ToString("N");
            _folder = "Assets/" + name;
            AssetDatabase.CreateFolder("Assets", name);

            _barrel = Create<TestBarrelSo>("Barrel");
            _turret = Create<TestTurretNodeSo>("Turret");
            _turret.barrel = _barrel;
            EditorUtility.SetDirty(_turret);
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

        float RecordedLength()
        {
            var reference = BlobchegBuild.RefsOf(_turret).Single();
            var file = Path.Combine(BlobchegBuild.OutputDirectory, TestCombatDb.FileName);
            var db = new TestCombatDb(BlobchegBuffer.From(File.ReadAllBytes(file), Allocator.Temp));
            try
            {
                return db.Read<TestTurret>(reference.Offset).BarrelLength;
            }
            finally
            {
                db.Dispose();
            }
        }

        /// <summary>The edit of a designer: a value in the foreign asset, saved to disk. The node stays untouched.</summary>
        void EditBarrel(float length)
        {
            _barrel.length = length;
            EditorUtility.SetDirty(_barrel);
            AssetDatabase.SaveAssets();
        }

        [Test]
        public void Editing_a_foreign_asset_reaches_the_record()
        {
            BlobchegBuild.RebuildAll();

            EditBarrel(42f);
            BlobchegBuild.RebuildAll();

            Assert.That(RecordedLength(), Is.EqualTo(42f),
                "the node was not touched, its dependency was — the record is obliged to follow the dependency");
        }

        [Test]
        public void A_reader_of_the_base_picks_up_the_foreign_edit()
        {
            BlobchegBuild.RebuildAll();

            EditBarrel(7f);
            BlobchegFreshness.Ensure("a test");

            Assert.That(RecordedLength(), Is.EqualTo(7f),
                "a save of the dependency alone moves the key — the reader is obliged to see the new value");
        }

        [Test]
        public void A_foreign_edit_lands_when_nothing_is_remembered()
        {
            BlobchegBuild.RebuildAll();

            // What a domain reload leaves behind: the index on disk and no memory of any node.
            BlobchegNodes.Forget();
            BlobchegFreshness.Forget();

            EditBarrel(13f);
            BlobchegFreshness.Ensure("a test");

            Assert.That(RecordedLength(), Is.EqualTo(13f),
                "the index outlives the domain — the edit is obliged to land with no memory at all");
        }

        [Test]
        public void Deleting_a_foreign_asset_reaches_the_record()
        {
            EditBarrel(5f);
            BlobchegBuild.RebuildAll();

            AssetDatabase.DeleteAsset(AssetDatabase.GetAssetPath(_barrel));
            BlobchegBuild.RebuildAll();

            Assert.That(RecordedLength(), Is.EqualTo(0f),
                "the dependency is gone — the record keeping its bytes would mean reading a deleted asset");
        }

        [Test]
        public void An_unsaved_edit_of_a_dependency_waits_for_the_save()
        {
            EditBarrel(3f);
            BlobchegBuild.RebuildAll();

            _barrel.length = 21f;
            EditorUtility.SetDirty(_barrel);
            BlobchegFreshness.Invalidate();
            BlobchegFreshness.Ensure("a test");

            Assert.That(RecordedLength(), Is.EqualTo(3f), "typing into a field is not a save: nothing is rebuilt");

            AssetDatabase.SaveAssetIfDirty(_barrel);
            BlobchegFreshness.Ensure("a test");

            Assert.That(RecordedLength(), Is.EqualTo(21f), "Ctrl+S is what the base follows");
        }

        [Test]
        public void An_unsaved_node_rebuilds_nothing_and_stays_unsaved()
        {
            EditBarrel(4f);
            BlobchegBuild.RebuildAll();

            var file = AssetDatabase.GetAssetPath(_turret);
            var bytes = File.ReadAllBytes(file);

            _turret.barrel = null;
            EditorUtility.SetDirty(_turret);
            BlobchegFreshness.Invalidate();

            Assert.That(BlobchegFreshness.Explain(), Is.EqualTo("the bases stand for the assets"),
                "an edit in memory is none of the package's business until it is saved");

            BlobchegBuild.RebuildAll();

            Assert.That(RecordedLength(), Is.EqualTo(4f), "the record keeps what was saved");
            Assert.That(File.ReadAllBytes(file), Is.EqualTo(bytes), "a rebuild writes no unsaved node to disk");
            Assert.That(EditorUtility.IsDirty(_turret), Is.True, "and leaves it unsaved for its owner");
        }

        [Test]
        public void A_new_node_gets_its_record()
        {
            BlobchegBuild.RebuildAll();

            var second = Create<TestTurretNodeSo>("Second");
            second.barrel = _barrel;
            EditorUtility.SetDirty(second);
            AssetDatabase.SaveAssets();

            BlobchegFreshness.Ensure("a test");

            Assert.That(BlobchegBuild.RefsOf(second).Count(), Is.EqualTo(1),
                "a node the walk has never seen is obliged to get an address without anyone asking");
        }

        [Test]
        public void A_deleted_node_takes_its_record_away()
        {
            var second = Create<TestTurretNodeSo>("Second");
            second.barrel = _barrel;
            EditorUtility.SetDirty(second);
            AssetDatabase.SaveAssets();
            BlobchegBuild.RebuildAll();

            var was = BlobchegBuild.RebuildAll().Records;
            AssetDatabase.DeleteAsset(_folder + "/Second.asset");
            BlobchegFreshness.Ensure("a test");

            Assert.That(BlobchegFreshness.Explain(), Is.EqualTo("the bases stand for the assets"),
                "the reader asked, so the rebuild is obliged to have happened already");

            Assert.That(BlobchegBuild.RebuildAll().Records, Is.EqualTo(was - 1),
                "the node is gone and its record is obliged to go with it");
        }

        [Test]
        public void A_rename_breaks_no_reader_and_moves_no_address()
        {
            EditBarrel(9f);
            BlobchegBuild.RebuildAll();

            var file = Path.Combine(BlobchegBuild.OutputDirectory, TestCombatDb.FileName);
            var was = File.ReadAllBytes(file);

            AssetDatabase.MoveAsset(_folder + "/Turret.asset", _folder + "/Renamed.asset");

            // The asset database may still be mid-move, and that is the window a reader lands in.
            BlobchegFreshness.Invalidate();
            Assert.DoesNotThrow(() => BlobchegFreshness.Ensure("a test"),
                "a rename is no reason to throw at whoever opened the base");

            Assert.That(File.ReadAllBytes(file), Is.EqualTo(was),
                "the layout is keyed by guid, so a rename is obliged to move no address at all");
        }

        [Test]
        public void A_dependency_of_a_dependency_reaches_the_record()
        {
            var deep = Create<TestBarrelSo>("Deep");
            var holder = Create<TestBarrelHolderSo>("Holder");
            holder.barrel = deep;

            var node = Create<TestHolderNodeSo>("HolderNode");
            node.holder = holder;
            EditorUtility.SetDirty(holder);
            EditorUtility.SetDirty(node);
            AssetDatabase.SaveAssets();

            BlobchegBuild.RebuildAll();

            deep.length = 17f;
            EditorUtility.SetDirty(deep);
            AssetDatabase.SaveAssets();

            BlobchegFreshness.Ensure("a test");

            Assert.That(LengthOf(node), Is.EqualTo(17f),
                "the node reads through two assets — the walk of dependencies is recursive for exactly this");
        }

        [Test]
        public void A_path_the_node_reads_past_a_reference_reaches_the_record()
        {
            var sidecar = _folder + "/Sidecar.txt";
            File.WriteAllText(sidecar, "5");
            AssetDatabase.ImportAsset(sidecar, ImportAssetOptions.ForceSynchronousImport);

            var node = Create<TestSidecarNodeSo>("SidecarNode");
            node.sidecarPath = sidecar;
            EditorUtility.SetDirty(node);
            AssetDatabase.SaveAssets();

            BlobchegBuild.RebuildAll();

            File.WriteAllText(sidecar, "11");
            AssetDatabase.ImportAsset(sidecar, ImportAssetOptions.ForceSynchronousImport);

            BlobchegFreshness.Ensure("a test");

            Assert.That(LengthOf(node), Is.EqualTo(11f),
                "no reference points at the file — CollectExtraDependencies is the only road the key has to it");
        }

        [Test]
        public void A_wiped_file_is_assembled_again()
        {
            BlobchegBuild.RebuildAll();

            var file = Path.Combine(BlobchegBuild.OutputDirectory, TestCombatDb.FileName);
            File.Delete(file);

            BlobchegFreshness.Invalidate();
            BlobchegFreshness.Ensure("a test");

            Assert.That(File.Exists(file), Is.True,
                "the files can be wiped past the assets, and then no node is dirty while the base is gone");
        }

        [Test]
        public void Nothing_changed_means_no_work_at_all()
        {
            BlobchegBuild.RebuildAll();
            BlobchegFreshness.Invalidate();

            Assert.That(BlobchegFreshness.Explain(), Is.EqualTo("the bases stand for the assets"),
                "an editor where nobody touched a node is obliged to hear nothing from the package");
        }

        float LengthOf(BlobchegNodeSo node)
        {
            var reference = BlobchegBuild.RefsOf(node).Single();
            var file = Path.Combine(BlobchegBuild.OutputDirectory, TestCombatDb.FileName);
            var db = new TestCombatDb(BlobchegBuffer.From(File.ReadAllBytes(file), Allocator.Temp));
            try
            {
                return db.Read<TestTurret>(reference.Offset).BarrelLength;
            }
            finally
            {
                db.Dispose();
            }
        }
    }
}

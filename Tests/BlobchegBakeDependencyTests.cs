using System;
using System.IO;
using System.Linq;
using Blobcheg.Authoring;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.AssetImporters;
using UnityEditor.Experimental;
using UnityEngine;

namespace Blobcheg.Tests
{
    // Stands in for a subscene import: loads the node the way a baker does, keeps the address it read.
    [ScriptedImporter(1, "blobchegbake")]
    public sealed class BlobchegBakeStandInImporter : ScriptedImporter
    {
        public static int Imports;

        public override void OnImportAsset(AssetImportContext ctx)
        {
            var nodePath = AssetDatabase.GUIDToAssetPath(File.ReadAllText(ctx.assetPath).Trim());
            ctx.DependsOnArtifact(new GUID(AssetDatabase.AssetPathToGUID(nodePath)));

            var reference = AssetDatabase.LoadAllAssetsAtPath(nodePath).OfType<BlobchegRefSo>().Single();
            ctx.AddObjectToAsset("main", new TextAsset(reference.Offset.ToString()));
            Imports++;
        }
    }

    [TestFixture(BlobchegTestMode.Editor)]
    [TestFixture(BlobchegTestMode.AsInPlayer)]
    public sealed class BlobchegBakeDependencyTests
    {
        readonly BlobchegTestMode _mode;

        string _folder;
        TestLootNodeSo _loot;
        TestPistolNodeSo _pistol;

        public BlobchegBakeDependencyTests(BlobchegTestMode mode) => _mode = mode;

        [SetUp]
        public void SetUp()
        {
            BlobchegTestModes.Enter(_mode);

            var name = "BlobchegBakeTemp_" + Guid.NewGuid().ToString("N");
            _folder = "Assets/" + name;
            AssetDatabase.CreateFolder("Assets", name);

            _loot = Create<TestLootNodeSo>("Loot");
            _pistol = Create<TestPistolNodeSo>("Pistol");
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
            return AssetDatabase.LoadAssetAtPath<T>(path);
        }

        string Bake(BlobchegNodeSo node)
        {
            var path = _folder + "/" + node.name + ".blobchegbake";
            File.WriteAllText(path, AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(node)));
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            return path;
        }

        // The way Entities asks for a subscene: the artifact on demand, no Refresh in between.
        static uint Baked(string path)
        {
            var key = new ArtifactKey(new GUID(AssetDatabase.AssetPathToGUID(path)), typeof(BlobchegBakeStandInImporter));
            AssetDatabaseExperimental.ProduceArtifact(key);
            return uint.Parse(AssetDatabase.LoadAssetAtPath<TextAsset>(path).text);
        }

        static uint Now(BlobchegNodeSo node) => BlobchegBuild.RefsOf(node).Single().Offset;

        [Test]
        public void A_moved_address_rebakes_what_read_it_and_the_node_file_stays()
        {
            BlobchegBuild.RebuildAll();
            var bake = Bake(_pistol);
            Assert.That(Baked(bake), Is.EqualTo(Now(_pistol)));

            var nodeFile = File.ReadAllText(AssetDatabase.GetAssetPath(_pistol));
            var before = Now(_pistol);

            // The loot table lies in front of the pistol: its growth pushes the pistol further.
            _loot.weights = new[] { 0.1f, 0.1f, 0.1f, 0.1f, 0.1f, 0.1f, 0.1f, 0.1f, 0.1f, 0.1f };
            EditorUtility.SetDirty(_loot);
            AssetDatabase.SaveAssetIfDirty(_loot);
            BlobchegBuild.RebuildAll();
            Assert.That(Now(_pistol), Is.Not.EqualTo(before), "the address did not move — the test checks nothing");

            Assert.That(Baked(bake), Is.EqualTo(Now(_pistol)), "the bake is obliged to hold the new address");
            Assert.That(File.ReadAllText(AssetDatabase.GetAssetPath(_pistol)), Is.EqualTo(nodeFile),
                "the address moved without a single byte of the node asset changing");
        }

        [Test]
        public void A_moved_address_saves_no_unsaved_node_and_catches_up_on_its_save()
        {
            BlobchegBuild.RebuildAll();
            var bake = Bake(_pistol);
            Baked(bake);

            var path = AssetDatabase.GetAssetPath(_pistol);
            var nodeFile = File.ReadAllText(path);

            // Unity saves every dirty asset at once, so the unsaved pistol edit comes after the loot save.
            _loot.weights = new[] { 0.1f, 0.1f, 0.1f, 0.1f, 0.1f, 0.1f, 0.1f, 0.1f, 0.1f, 0.1f };
            EditorUtility.SetDirty(_loot);
            AssetDatabase.SaveAssetIfDirty(_loot);
            _pistol.rpm = 900;
            EditorUtility.SetDirty(_pistol);
            BlobchegBuild.RebuildAll();

            Assert.That(File.ReadAllText(path), Is.EqualTo(nodeFile), "the rebuild is obliged not to save someone's edit");
            Assert.That(EditorUtility.IsDirty(_pistol), Is.True);

            AssetDatabase.SaveAssetIfDirty(_pistol);
            BlobchegFreshness.Ensure("a test");

            Assert.That(Baked(bake), Is.EqualTo(Now(_pistol)), "after the save the bake holds the new address");
        }

        [Test]
        public void An_unmoved_address_rebakes_nothing()
        {
            BlobchegBuild.RebuildAll();
            var bake = Bake(_pistol);
            Baked(bake);

            // A neighbour in front changes a value, not a length: the pistol stays where it was.
            var imports = BlobchegBakeStandInImporter.Imports;
            _loot.rolls = 7;
            EditorUtility.SetDirty(_loot);
            AssetDatabase.SaveAssetIfDirty(_loot);
            BlobchegBuild.RebuildAll();

            Baked(bake);
            Assert.That(BlobchegBakeStandInImporter.Imports, Is.EqualTo(imports),
                "a value edit of a neighbour moves no address, and the bake of that address is not redone");
        }
    }
}

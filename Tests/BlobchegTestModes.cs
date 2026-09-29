using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Blobcheg.Authoring;
using NUnit.Framework;
using Unity.Collections;
using UnityEditor;
using UnityEngine;

namespace Blobcheg.Tests
{
    public enum BlobchegTestMode
    {
        Editor,
        AsInPlayer,
    }

    public static class BlobchegTestModes
    {
        // The contour and not the EditorPrefs toggle: a test run must not flip the mode of the machine.
        public static void Enter(BlobchegTestMode mode) => BlobchegBuild.DebugContour = mode == BlobchegTestMode.Editor;

        public static void Leave() => BlobchegBuild.DebugContour = !BlobchegBuild.AsInPlayer;

        public static bool HasContour(string domainName)
        {
            var path = Path.Combine(BlobchegBuild.OutputDirectory, BlobchegNaming.FileName(domainName));
            var blob = new BlobchegBlob(BlobchegBuffer.From(File.ReadAllBytes(path), Allocator.Temp), domainName);
            try
            {
                return blob.HasDebug;
            }
            finally
            {
                blob.Dispose();
            }
        }
    }

    public sealed class BlobchegModeTests
    {
        const string Combat = nameof(ITestCombatData);

        string _folder;
        TestModuleNodeSo _module;
        TestColdOnlyNodeSo _cold;
        TestTurretNodeSo _turret;
        TestBarrelSo _barrel;

        [SetUp]
        public void SetUp()
        {
            var name = "BlobchegModeTemp_" + Guid.NewGuid().ToString("N");
            _folder = "Assets/" + name;
            AssetDatabase.CreateFolder("Assets", name);

            _module = Create<TestModuleNodeSo>("Module");
            _cold = Create<TestColdOnlyNodeSo>("ColdOnly");
            _barrel = Create<TestBarrelSo>("Barrel");
            _turret = Create<TestTurretNodeSo>("Turret");
            _barrel.length = 3.5f;
            _turret.barrel = _barrel;
            EditorUtility.SetDirty(_barrel);
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

        Dictionary<string, uint> Addresses()
        {
            var addresses = new Dictionary<string, uint>(StringComparer.Ordinal);

            foreach (BlobchegNodeSo node in new BlobchegNodeSo[] { _module, _cold, _turret })
            {
                foreach (var reference in BlobchegBuild.RefsOf(node))
                    addresses[reference.name] = reference.Offset;

                foreach (var carrier in BlobchegBuild.IdsOf(node))
                    addresses[carrier.name] = new BlobchegIdRef<TestGameRouter>(carrier).Id.Value;
            }

            return addresses;
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

        [Test]
        public void The_player_mode_moves_no_address_and_no_id()
        {
            BlobchegTestModes.Enter(BlobchegTestMode.Editor);
            BlobchegBuild.RebuildAll();
            Assert.That(BlobchegTestModes.HasContour(Combat), Is.True,
                "the editor mode writes the contour — the test is checking the wrong thing otherwise");
            var editor = Addresses();

            BlobchegTestModes.Enter(BlobchegTestMode.AsInPlayer);
            BlobchegBuild.RebuildAll();
            Assert.That(BlobchegTestModes.HasContour(Combat), Is.False,
                "the player mode is obliged to write the files the way a release build does");

            // A subscene baked in one mode is read in the other: the contour lies behind the records.
            Assert.That(Addresses(), Is.EquivalentTo(editor));
            Assert.That(RecordedLength(), Is.EqualTo(3.5f));
        }

        [Test]
        public void A_reader_rebuilds_by_itself_when_the_mode_flips()
        {
            BlobchegTestModes.Enter(BlobchegTestMode.Editor);
            BlobchegBuild.RebuildAll();

            BlobchegTestModes.Enter(BlobchegTestMode.AsInPlayer);
            BlobchegFreshness.Invalidate();
            BlobchegTransport.Default.Read(TestCombatDb.FileName, Allocator.Temp).Dispose();
            Assert.That(BlobchegTestModes.HasContour(Combat), Is.False,
                "the reader asked for a base in the player mode and got the editor one");

            BlobchegTestModes.Enter(BlobchegTestMode.Editor);
            BlobchegFreshness.Invalidate();
            BlobchegTransport.Default.Read(TestCombatDb.FileName, Allocator.Temp).Dispose();
            Assert.That(BlobchegTestModes.HasContour(Combat), Is.True,
                "the way back is obliged to give the contour back by itself");
        }
    }
}

using System;
using System.IO;
using System.Linq;
using Blobcheg.Authoring;
using NUnit.Framework;
using Unity.Collections;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blobcheg.AdvancedTests
{
    /// <summary>
    /// The shared rig of the destructive set. It holds two roads into the binary and both are
    /// end-to-end:
    ///
    /// 1. THE EDITOR CYCLE: node assets → <see cref="BlobchegBuild.RebuildAll"/> → files in
    ///    StreamingAssets → ref/id assets → a read by reinterpretation. That is how a consumer lives.
    /// 2. THE FILE CYCLE: <see cref="BlobchegWriter"/>/<see cref="BlobchegRouterWriter"/> → bytes on
    ///    disk → <see cref="BlobchegBlob"/>/<see cref="BlobchegRouterBlob"/>. With the same input and
    ///    output but without assets — otherwise the volume and boundary cases (64 bases, 100k rows)
    ///    would cost tens of thousands of assets.
    ///
    /// The set does not climb inside records or inside the layout: it looks only at what is visible from
    /// the outside.
    /// </summary>
    public abstract class AdvancedFixture
    {
        /// <summary>Everything the rebuild lays down because of the domains and routers of this assembly.</summary>
        static readonly string[] Artifacts =
        {
            "IAdvCombat", "IAdvCold", "IAdvLoose", "IAdvOther", "AdvRouter", "AdvAlienRouter",
        };

        protected string Folder;
        protected string Scratch;

        [SetUp]
        public void AdvancedSetUp()
        {
            // A folder of its own per test: asset deletion is deferred, and a reused name swallows an
            // asset created in a folder that has not been deleted yet.
            var name = "BlobchegAdvanced_" + Guid.NewGuid().ToString("N");
            Folder = "Assets/" + name;
            AssetDatabase.CreateFolder("Assets", name);

            Scratch = Path.Combine(Path.GetTempPath(), name);
            Directory.CreateDirectory(Scratch);

            AdvReentrantNodeSo.Forget();

            // The set breaks the rebuild on purpose, and the import hook calls it itself with a deferred
            // call — that is, between tests. An error arriving from there must not fail the NEIGHBOURING
            // test: what exactly failed is checked by every test itself, with its own Assert.Throws.
            LogAssert.ignoreFailingMessages = true;
        }

        [TearDown]
        public void AdvancedTearDown()
        {
            AssetDatabase.DeleteAsset(Folder);

            foreach (var name in Artifacts)
            {
                AssetDatabase.DeleteAsset(BlobchegBuild.ManifestFolder + "/" + name + ".asset");

                var file = FileOf(name);
                if (File.Exists(file))
                    File.Delete(file);
            }

            AssetDatabase.Refresh();

            try
            {
                if (Directory.Exists(Scratch))
                    Directory.Delete(Scratch, true);
            }
            catch (IOException)
            {
                // Rubbish in the OS temp folder does not fail the test.
            }

            LogAssert.ignoreFailingMessages = false;
        }

        // ------------------------------------------------------------- assets

        protected T Node<T>(string name) where T : BlobchegNodeSo
        {
            var path = Folder + "/" + name + ".asset";
            AssetDatabase.CreateAsset(ScriptableObject.CreateInstance<T>(), path);

            var asset = AssetDatabase.LoadAssetAtPath<T>(path);
            Assert.That(asset, Is.Not.Null, $"asset '{path}' was not created — there is nothing further to check");
            return asset;
        }

        /// <summary>A node in a subfolder — so that two nodes with the SAME name can be created.</summary>
        protected T NodeIn<T>(string subFolder, string name) where T : BlobchegNodeSo
        {
            if (!AssetDatabase.IsValidFolder(Folder + "/" + subFolder))
                AssetDatabase.CreateFolder(Folder, subFolder);

            var path = Folder + "/" + subFolder + "/" + name + ".asset";
            AssetDatabase.CreateAsset(ScriptableObject.CreateInstance<T>(), path);

            var asset = AssetDatabase.LoadAssetAtPath<T>(path);
            Assert.That(asset, Is.Not.Null, $"asset '{path}' was not created — there is nothing further to check");
            return asset;
        }

        protected static void Dirty(UnityEngine.Object asset)
        {
            EditorUtility.SetDirty(asset);
            AssetDatabase.SaveAssets();
        }

        protected static void Kill(UnityEngine.Object asset)
        {
            AssetDatabase.DeleteAsset(AssetDatabase.GetAssetPath(asset));
            AssetDatabase.Refresh();
        }

        protected static BlobchegBuildReport Rebuild() => BlobchegBuild.RebuildAll();

        /// <summary>
        /// Takes a copy of a <c>ref readonly</c> return. Needed where a record has not a single field
        /// that could be touched by an expression — otherwise there is nothing to "use" a <c>Read</c>
        /// call with.
        /// </summary>
        protected static T Copy<T>(in T value) where T : unmanaged => value;

        // ------------------------------------------------------------- addresses

        /// <summary>The offset of a node's record in a domain — by the same path a consumer's baker takes it.</summary>
        protected static uint OffsetOf(BlobchegNodeSo node, string domainName)
        {
            var reference = BlobchegBuild.RefsOf(node).SingleOrDefault(r => r.DomainName == domainName);
            Assert.That(reference, Is.Not.Null, $"node '{node.name}' has no ref asset for domain '{domainName}'");
            return reference.offset;
        }

        protected static BlobchegRefSo RefOf(BlobchegNodeSo node, string domainName)
            => BlobchegBuild.RefsOf(node).Single(r => r.DomainName == domainName);

        protected static BlobchegId IdOf(BlobchegNodeSo node, string routerName)
        {
            var carrier = BlobchegBuild.IdsOf(node).SingleOrDefault(c => c.RouterName == routerName);
            Assert.That(carrier, Is.Not.Null, $"node '{node.name}' has no id carrier for router '{routerName}'");
            return new BlobchegId(carrier.id);
        }

        // ------------------------------------------------------------- files

        protected static string FileOf(string domainOrRouter)
            => Path.Combine(BlobchegBuild.OutputDirectory, BlobchegNaming.FileName(domainOrRouter));

        protected static byte[] Bytes(string domainOrRouter) => File.ReadAllBytes(FileOf(domainOrRouter));

        protected static void Overwrite(string domainOrRouter, byte[] file)
            => File.WriteAllBytes(FileOf(domainOrRouter), file);

        /// <summary>
        /// Re-stamps the header over a changed body. Needed where the MEANING of the file is broken (the
        /// prolog, the flags, the length) rather than its integrity: without the re-stamp the hash would
        /// fire first and the test would check something other than what it meant to.
        /// </summary>
        protected static void Reseal(byte[] file)
        {
            var flags = BlobchegBytes.ReadU16(file, 6);
            var debugOffset = BlobchegBytes.ReadU32(file, 12);
            var nameHash = BlobchegBytes.ReadU64(file, 24);
            BlobchegBytes.Seal(file, flags, debugOffset, nameHash);
        }

        protected static BlobchegBuffer BufferOf(string fileName)
            => BlobchegBuffer.From(
                File.ReadAllBytes(Path.Combine(BlobchegBuild.OutputDirectory, fileName)), Allocator.Persistent);

        protected static AdvCombatDb Combat() => new AdvCombatDb(BufferOf(AdvCombatDb.FileName));

        protected static AdvColdDb Cold() => new AdvColdDb(BufferOf(AdvColdDb.FileName));

        protected static AdvLooseDb Loose() => new AdvLooseDb(BufferOf(AdvLooseDb.FileName));

        protected static AdvOtherDb Other() => new AdvOtherDb(BufferOf(AdvOtherDb.FileName));

        protected static AdvRouter Router() => new AdvRouter(BufferOf(AdvRouter.FileName));

        protected static AdvAlienRouter OtherRouter() => new AdvAlienRouter(BufferOf(AdvAlienRouter.FileName));
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using Blobcheg.Authoring;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Blobcheg.AdvancedTests
{
    /// <summary>
    /// The identity of a record. The package has exactly two addresses — an offset and a
    /// <see cref="BlobchegId"/> — and both are bare numbers. What is checked here is what happens if a
    /// number is given to someone it does not belong to.
    /// </summary>
    public sealed class IdentityTests : AdvancedFixture
    {
        /// <summary>
        /// The kinship of an id rests on the tag — the high byte of the value. Without it a bare number
        /// would fall into the range of a neighbouring router and hand out a foreign row silently: the
        /// kinship check would exist only at the asset level, while in a component, in a save and on the
        /// wire all that is left of an id is a uint.
        /// </summary>
        [Test]
        public void An_id_of_a_foreign_router_does_not_resolve_in_this_one()
        {
            Node<AdvComboNodeSo>("Combo");
            var other = Node<AdvOtherNodeSo>("Other");
            Rebuild();

            var alien = IdOf(other, AdvAlienRouter.RouterName);

            var router = Router();
            try
            {
                Assert.That(alien.Index, Is.EqualTo(0u), "the row is the very same one — only the tag tells them apart");
                Assert.That(router.Count, Is.GreaterThan((int)alien.Index),
                    "and it exists in this router, otherwise the test would have caught an ordinary out-of-range");

                Assert.Throws<InvalidOperationException>(() => router.Get(alien),
                    "this id was handed out by the AdvAlienRouter router — in AdvRouter it means nothing");
                Assert.That(router.TryGet(alien, out _), Is.False);
            }
            finally
            {
                router.Dispose();
            }
        }

        [Test]
        public void An_id_carries_the_tag_of_its_own_router()
        {
            var combo = Node<AdvComboNodeSo>("Combo");
            var other = Node<AdvOtherNodeSo>("Other");
            Rebuild();

            var mine = IdOf(combo, AdvRouter.RouterName);
            var alien = IdOf(other, AdvAlienRouter.RouterName);

            Assert.That(mine.Tag, Is.Not.Zero, "tag zero is reserved for \"id not assigned\"");
            Assert.That(mine.Tag, Is.Not.EqualTo(alien.Tag), "two routers mean two different tags");
            Assert.That(mine.Tag, Is.EqualTo(BlobchegNaming.TagOf(AdvRouter.RouterName)),
                "the tag is derived from the router name, so the editor and the file arrive at it independently");

            var router = Router();
            try
            {
                Assert.That(router.Tag, Is.EqualTo(mine.Tag));
                Assert.That(router.IdAt(mine.Index), Is.EqualTo(mine), "walking the router gives the same ids");
            }
            finally
            {
                router.Dispose();
            }
        }

        [Test]
        public void A_carrier_of_a_foreign_router_is_rejected_by_the_field()
        {
            Node<AdvComboNodeSo>("Combo");
            var other = Node<AdvOtherNodeSo>("Other");
            Rebuild();

            var alienCarrier = BlobchegBuild.IdsOf(other).Single(c => c.RouterName == AdvAlienRouter.RouterName);

            var thrown = Assert.Throws<InvalidOperationException>(
                () => { _ = new BlobchegIdRef<AdvRouter>(alienCarrier).Id; },
                "the asset of a foreign router in a typed field is obliged to be rejected");
            StringAssert.Contains(AdvRouter.RouterName, thrown.Message);

            var empty = new BlobchegIdRef<AdvRouter>(null);
            Assert.That(empty.IsSet, Is.False);
            Assert.Throws<InvalidOperationException>(() => { _ = empty.Id; },
                "an empty field is an error and not id zero");
        }

        [Test]
        public void A_ref_of_a_foreign_domain_is_rejected_by_a_typed_field()
        {
            var loose = Node<AdvLooseNodeSo>("Loose");
            Node<AdvComboNodeSo>("Combo");
            Rebuild();

            var looseRef = RefOf(loose, "IAdvLoose");

            var thrown = Assert.Throws<InvalidOperationException>(
                () => { _ = new BlobchegRef<AdvGun>(looseRef).Offset; },
                "a record of a different type lies in a BlobchegRef<AdvGun> field — that is an error");
            StringAssert.Contains(nameof(AdvGun), thrown.Message);

            Assert.That(new BlobchegRef<AdvLooseBlock>(looseRef).Offset, Is.EqualTo(looseRef.offset),
                "while its own type is obliged to pass");
        }

        /// <summary>
        /// An offset carries no identity and cannot: it is a position in a file and not a name. What
        /// catches a foreign address is the debug contour — through it one can see whether a record
        /// starts at that place and whether it is the right one. In the editor and in a development build
        /// the contour is always there, in a release player it is not, and there it is once again a
        /// question of trust — exactly like the rest of the content of a record.
        /// </summary>
        [Test]
        public void An_offset_from_a_foreign_base_does_not_read_in_this_one()
        {
            var loose = Node<AdvLooseNodeSo>("Loose");
            Node<AdvComboNodeSo>("Combo");
            Node<AdvArmorNodeSo>("Armor");
            Rebuild();

            var alienOffset = OffsetOf(loose, "IAdvLoose");

            var db = Combat();
            try
            {
                Assert.That(db.HasDebug, Is.True, "in the editor the debug contour is obliged to be there — the check stands on it");
                Assert.That(alienOffset + 8u, Is.LessThanOrEqualTo((uint)db.Length),
                    "the address is obliged to fit into the combat base, otherwise the test would have caught an ordinary out-of-bounds");

                Assert.Throws<InvalidOperationException>(() => { _ = db.Read<AdvGun>(alienOffset).Rpm; },
                    "this address was handed out by the IAdvLoose base — in the combat base anything lies at it");
            }
            finally
            {
                db.Dispose();
            }
        }

        /// <summary>
        /// The FILE itself, however, does have an identity — the hash of the domain name in the header.
        /// Without it two .bcheg files swapped with each other both come up: each has its own integrity
        /// and each adds up.
        /// </summary>
        [Test]
        public void A_file_of_a_foreign_base_does_not_load_under_this_name()
        {
            Node<AdvLooseNodeSo>("Loose");
            Node<AdvComboNodeSo>("Combo");
            Rebuild();

            // We swap the combat base file with the cold one — exactly what an unlucky merge or a manual
            // copy of the "ah, it is just data" kind does.
            Overwrite("IAdvCombat", Bytes("IAdvCold"));

            var buffer = BufferOf(AdvCombatDb.FileName);
            try
            {
                var thrown = Assert.Throws<InvalidOperationException>(() => { _ = new AdvCombatDb(buffer); },
                    "the file is whole and the hash adds up — but it is the file of another domain");
                StringAssert.Contains("another domain", thrown.Message);
            }
            finally
            {
                buffer.Dispose();
            }
        }

        [Test]
        public void A_node_outside_a_base_answers_with_the_absence_of_a_record()
        {
            Node<AdvComboNodeSo>("Combo");
            var cold = Node<AdvColdOnlyNodeSo>("Cold");
            Rebuild();

            var id = IdOf(cold, AdvRouter.RouterName);

            var router = Router();
            try
            {
                var row = router.Get(id);
                Assert.That(row.HasCold, Is.True);
                Assert.That(row.HasCombat, Is.False);

                Assert.Throws<InvalidOperationException>(() => { _ = row.combat; },
                    "there can be no \"no record\" sentinel: a silent zero would travel into Read");
                Assert.That(router.TryGetCombat(id, out _), Is.False);
                Assert.That(router.HasCombat(id), Is.False);
            }
            finally
            {
                router.Dispose();
            }
        }

        /// <summary>
        /// Zero initialisation is ANY default: an IComponentData field, a NativeArray element, an unset
        /// struct field. Zero was chosen as the sentinel for exactly that reason: a forgotten field is
        /// obliged to fail and not to lead quietly to the first node of a router.
        /// </summary>
        [Test]
        public void A_default_BlobchegId_is_not_valid()
        {
            Node<AdvComboNodeSo>("Combo");
            Rebuild();

            Assert.That(default(BlobchegId).IsValid, Is.False,
                "an unfilled field means \"not set\" and not row zero");

            var router = Router();
            try
            {
                Assert.That(router.Count, Is.EqualTo(1), "row zero does exist in the router all the same");
                Assert.That(router.TryGet(default, out _), Is.False,
                    "a zero id has no right to hand out the first node of a base");
                Assert.Throws<InvalidOperationException>(() => router.Get(default));
            }
            finally
            {
                router.Dispose();
            }
        }

        [Test]
        public void Editing_a_value_moves_neither_the_id_nor_the_offset()
        {
            var node = Node<AdvComboNodeSo>("Combo");
            node.tier = 1;
            Dirty(node);
            Rebuild();

            var idBefore = IdOf(node, AdvRouter.RouterName);
            var offsetBefore = OffsetOf(node, "IAdvCold");

            node.tier = 12345;
            Dirty(node);
            Rebuild();

            Assert.That(IdOf(node, AdvRouter.RouterName), Is.EqualTo(idBefore), "an id is a position and not a hash of the content");
            Assert.That(OffsetOf(node, "IAdvCold"), Is.EqualTo(offsetBefore), "the size did not change, so neither did the address");

            var cold = Cold();
            try
            {
                Assert.That(cold.Read<AdvColdInfo>(offsetBefore).Tier, Is.EqualTo(12345),
                    "while the value was obliged to change");
            }
            finally
            {
                cold.Dispose();
            }
        }

        /// <summary>
        /// Renamed and rebuilt right away — that is how any script and the pre-build gate go. In that
        /// editor run the asset database has not digested the rename yet: the asset loads neither under
        /// the old path nor under the new one, and the search index knows nothing about it — verified by
        /// measurement, <c>ImportAsset(ForceSynchronousImport)</c> and <c>Refresh</c> do not fix that.
        ///
        /// So there are exactly two outcomes and both are obliged to be honest: either the walk sees the
        /// node or the rebuild REFUSES. What must not happen is the third: a base silently assembled
        /// without its record and with the ids of its neighbours shifted. The asset lies on disk and its
        /// GUID is known, so the package does have something to tell a loss from a deletion by.
        /// </summary>
        [Test]
        public void Renaming_a_node_does_not_lose_it_silently()
        {
            var a = Node<AdvColdOnlyNodeSo>("Alpha");
            Node<AdvColdOnlyNodeSo>("Beta");
            Rebuild();

            var before = IdOf(a, AdvRouter.RouterName);

            // The GUID is taken BEFORE the rename: the managed wrapper of a renamed asset does not survive
            // a reimport, and holding on to it is a mistake of the test and not a finding about the package.
            var guid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(a));
            Assert.That(AssetDatabase.RenameAsset(AssetDatabase.GetAssetPath(a), "Zulu"), Is.Empty,
                "the rename is obliged to go through, otherwise there is nothing further to check");
            AssetDatabase.SaveAssets();

            var renamedPath = Folder + "/Zulu.asset";
            AssetDatabase.ImportAsset(renamedPath, ImportAssetOptions.ForceSynchronousImport);

            Assert.That(AssetDatabase.AssetPathToGUID(renamedPath), Is.EqualTo(guid),
                "the GUID of a renamed asset is obliged to stay the same — the id rests on it");

            List<BlobchegNodeSo> seen = null;
            InvalidOperationException refused = null;
            try
            {
                seen = BlobchegBuild.FindNodes();
            }
            catch (InvalidOperationException e)
            {
                refused = e;
            }

            if (refused != null)
            {
                StringAssert.Contains("Zulu", refused.Message, "the refusal is obliged to name the node it happened because of");
                Assert.Throws<InvalidOperationException>(() => Rebuild(),
                    "and the rebuild itself in this state is obliged to refuse the same way and not to assemble a base without the node");

                // We remove the asset ourselves: while it lies undigested, the walk will keep refusing.
                AssetDatabase.DeleteAsset(renamedPath);
                AssetDatabase.Refresh();
                return;
            }

            // We look for the node BY PATH and not by name: the name of a managed object is updated
            // lazily after a rename.
            var renamed = seen.FirstOrDefault(n => AssetDatabase.GetAssetPath(n) == renamedPath);
            Assert.That(renamed, Is.Not.Null,
                "the walk did not refuse — so it was obliged to see the node; it returned: " +
                string.Join(", ", seen.Select(n => AssetDatabase.GetAssetPath(n))));

            Rebuild();
            Assert.That(IdOf(renamed, AdvRouter.RouterName), Is.EqualTo(before),
                "an id is computed from the asset GUID — the name has no right to affect it");
        }

        /// <summary>
        /// THE PREMISE MOVED together with the table of name hashes. The identity of a node used to be the
        /// GUID alone and identical names were lawful; now the name of a node is the address of its record
        /// in a save, and two nodes with one name in a router would lay one hash across two rows. The
        /// rebuild is obliged to refuse out loud and to name both.
        /// </summary>
        [Test]
        public void Nodes_with_identical_names_are_rejected_out_loud()
        {
            var one = Node<AdvColdOnlyNodeSo>("Same");
            var two = NodeIn<AdvColdOnlyNodeSo>("Nested", "Same");
            Dirty(one);
            Dirty(two);

            var thrown = Assert.Throws<InvalidOperationException>(() => Rebuild());
            StringAssert.Contains("'Same'", thrown.Message);
            StringAssert.Contains("Nested/Same.asset", thrown.Message, "both namesake nodes are named");
        }

        [Test]
        public void A_node_in_two_routers_requires_an_explicit_IdIn()
        {
            var node = Node<AdvBothRoutersNodeSo>("Both");
            node.askSingleId = true;
            Dirty(node);

            Assert.Throws<InvalidOperationException>(() => Rebuild(),
                "the node has two routers — \"its one and only id\" cannot be asked of it, that must be an error");

            node.askSingleId = false;
            Dirty(node);
            Rebuild();

            var mine = IdOf(node, AdvRouter.RouterName);
            var alien = IdOf(node, AdvAlienRouter.RouterName);

            Assert.That(node.LastMain, Is.EqualTo(mine.Value), "IdIn handed out the same id that travelled into the carrier");
            Assert.That(node.LastOther, Is.EqualTo(alien.Value));
            Assert.That(BlobchegBuild.IdsOf(node).Count(), Is.EqualTo(2), "one carrier per router");
        }

        [Test]
        public void Ids_are_dense_and_contiguous()
        {
            var nodes = new BlobchegNodeSo[]
            {
                Node<AdvComboNodeSo>("A"),
                Node<AdvColdOnlyNodeSo>("B"),
                Node<AdvColdOnlyNodeSo>("C"),
                Node<AdvArmorNodeSo>("D"),
            };

            Rebuild();

            var ids = nodes.Select(n => IdOf(n, AdvRouter.RouterName).Index).OrderBy(v => v).ToArray();
            CollectionAssert.AreEqual(new uint[] { 0, 1, 2, 3 }, ids,
                "a row is a dense index; holes in it would make array[index] impossible");

            var router = Router();
            try
            {
                Assert.That(router.Count, Is.EqualTo(nodes.Length));
            }
            finally
            {
                router.Dispose();
            }
        }

        [Test]
        public void The_router_and_the_ref_asset_give_one_address()
        {
            var node = Node<AdvComboNodeSo>("Combo");
            Node<AdvArmorNodeSo>("Armor");
            Rebuild();

            var viaRef = OffsetOf(node, "IAdvCombat");
            var id = IdOf(node, AdvRouter.RouterName);

            var router = Router();
            try
            {
                Assert.That(router.GetCombat(id), Is.EqualTo(viaRef),
                    "the two addresses of the package are obliged to meet on one record, otherwise there are two different ones");
                Assert.That(router.Get(id).combat, Is.EqualTo(viaRef));
            }
            finally
            {
                router.Dispose();
            }
        }
    }
}

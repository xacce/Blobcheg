using System;
using System.IO;
using System.Linq;
using Blobcheg.Authoring;
using NUnit.Framework;
using Unity.Collections;
using UnityEditor;

namespace Blobcheg.AdvancedTests
{
    /// <summary>
    /// The human factor. Not an attacker and not the edge of a range but ordinary habits: put an id into
    /// a save, cache an offset, check "is it non-zero", trust the manifest, fix a couple of bytes in an
    /// assembled file by hand. These scenarios cost production more often than all the boundaries put
    /// together.
    /// </summary>
    public sealed class HumanFactorTests : AdvancedFixture
    {
        /// <summary>
        /// The most expensive breakage possible: a player opens a save and gets the wrong item, without a
        /// single error. It is closed by the fact that an id is not recomputed — it lies on the node's
        /// carrier and is inherited, while a deleted node leaves an empty row behind. A hole row costs a
        /// few bytes in the file; pulling the next one in would mean shifting someone else's saved id.
        /// </summary>
        [Test]
        public void A_saved_id_after_a_neighbour_is_deleted_does_not_point_at_another_node()
        {
            var created = new[]
            {
                Node<AdvColdOnlyNodeSo>("N1"),
                Node<AdvColdOnlyNodeSo>("N2"),
                Node<AdvColdOnlyNodeSo>("N3"),
            };

            Rebuild();

            var byId = created.OrderBy(n => IdOf(n, AdvRouter.RouterName).Index).ToArray();
            for (var i = 0; i < byId.Length; i++)
            {
                byId[i].tier = 100 * (i + 1);
                Dirty(byId[i]);
            }

            Rebuild();

            // This is what a consumer does: took the id and put it into a save. From there on they see only a number.
            var saved = IdOf(byId[1], AdvRouter.RouterName);
            Assert.That(saved.Index, Is.EqualTo(1u));

            Kill(byId[0]);
            Rebuild();

            var router = Router();
            var cold = Cold();
            try
            {
                Assert.That(router.Count, Is.EqualTo(3),
                    "the deleted node left a hole: there are still three rows and the first one is empty");
                Assert.That(router.HasCold(BlobchegId.In(AdvRouter.RouterName, 0)), Is.False, "and it is empty");

                Assert.That(IdOf(byId[1], AdvRouter.RouterName), Is.EqualTo(saved),
                    "the neighbour's id does not slide down after the deleted one");
                Assert.That(cold.Read<AdvColdInfo>(router.GetCold(saved)).Tier, Is.EqualTo(200),
                    "a saved id is obliged to lead to its own node");
            }
            finally
            {
                router.Dispose();
                cold.Dispose();
            }
        }

        /// <summary>
        /// A consumer will cache an address — in a component, in a static, in a baked subscene. That is
        /// why an address is pinned to its record: the previous address arrives into the layout as a claim
        /// from the node's carrier, and the appearance of a neighbour does not move it. Only a compaction
        /// moves it — and it is a separate command precisely because everything that remembered the
        /// address gets rebaked after it.
        /// </summary>
        [Test]
        public void A_cached_offset_outlives_the_appearance_of_a_foreign_record()
        {
            var gun = Node<AdvComboNodeSo>("Combo");
            gun.rpm = 999;
            Dirty(gun);
            Rebuild();

            // The consumer cached the address on their side: put it into a component, a static, a save — it does not matter.
            var cached = OffsetOf(gun, "IAdvCombat");

            // A FOREIGN node appeared with a type whose name sorts BEFORE AdvGun: without a claim on the
            // previous address it would shift every record of the following types.
            Node<AdvArmorNodeSo>("Armor");
            Rebuild();

            Assert.That(OffsetOf(gun, "IAdvCombat"), Is.EqualTo(cached),
                "a new node has no right to move someone else's address");

            var db = Combat();
            try
            {
                Assert.That(db.Read<AdvGun>(cached).Rpm, Is.EqualTo(999),
                    "and the same record lies at the cached address");
            }
            finally
            {
                db.Dispose();
            }
        }

        /// <summary>
        /// The other side: a compaction moves addresses on purpose. What matters here is that it does not
        /// leave the consumer with a stale number silently — the carriers are rewritten, and at the old
        /// address either the wrong record lies or nothing does.
        /// </summary>
        [Test]
        public void A_compaction_moves_the_address_and_rewrites_the_carrier()
        {
            var armor = Node<AdvArmorNodeSo>("Armor");
            var gun = Node<AdvComboNodeSo>("Combo");
            Rebuild();

            Kill(armor);
            Rebuild();

            var withHole = OffsetOf(gun, "IAdvCombat");

            BlobchegBuild.Compact();

            var compacted = OffsetOf(gun, "IAdvCombat");
            Assert.That(compacted, Is.LessThan(withHole), "the compaction removed the hole and pulled the record in");
            Assert.That(compacted, Is.EqualTo((uint)BlobchegFormat.HeaderSize),
                "after a compaction the only record lies right after the header");

            var db = Combat();
            try
            {
                Assert.That(db.Read<AdvGun>(compacted).Rpm, Is.EqualTo(600), "it reads at the new address");
                Assert.Throws<InvalidOperationException>(() => { _ = db.Read<AdvGun>(withHole).Rpm; },
                    "and no longer at the old one, and that is visible rather than silent");
            }
            finally
            {
                db.Dispose();
            }
        }

        [Test]
        public void The_id_order_is_reproducible_across_rebuilds()
        {
            var created = new BlobchegNodeSo[]
            {
                Node<AdvColdOnlyNodeSo>("Zulu"),
                Node<AdvColdOnlyNodeSo>("Alpha"),
                Node<AdvComboNodeSo>("Mike"),
            };

            Rebuild();
            var first = created.Select(n => IdOf(n, AdvRouter.RouterName).Value).ToArray();

            Rebuild();
            Rebuild();

            var again = created.Select(n => IdOf(n, AdvRouter.RouterName).Value).ToArray();

            CollectionAssert.AreEqual(first, again,
                "the id order is obliged to be a function of the project and not of the traversal order: otherwise the build and the editor drift apart");
        }

        /// <summary>
        /// The habit "if (id != 0)" is the most widespread check in the world, and here it is obliged to
        /// work. It works because tag zero is reserved: row zero exists, but its id is never zero.
        /// </summary>
        [Test]
        public void The_familiar_check_against_zero_works()
        {
            var created = new[]
            {
                Node<AdvColdOnlyNodeSo>("A"),
                Node<AdvColdOnlyNodeSo>("B"),
            };

            Rebuild();

            var ids = created.Select(n => IdOf(n, AdvRouter.RouterName)).ToArray();
            Assert.That(ids.Count(id => id.Index == 0), Is.EqualTo(1), "row zero exists, as before");
            Assert.That(ids.Count(id => id.Value == 0), Is.Zero, "while id zero is handed out to nobody");

            Assert.That(BlobchegId.None.IsValid, Is.False);
            Assert.That(BlobchegId.None.Value, Is.Zero, "\"not assigned\" and zero are the same value");
            Assert.That(new BlobchegId(0).IsValid, Is.False);
        }

        [Test]
        public void A_domain_manifest_is_no_proof_that_it_is_built()
        {
            Node<AdvComboNodeSo>("Combo");
            Rebuild();

            var manifest = AssetDatabase.LoadAssetAtPath<BlobchegDomainSo>(
                BlobchegBuild.ManifestFolder + "/IAdvCombat.asset");

            Assert.That(manifest, Is.Not.Null, "the manifest is what a developer sees with their eyes in the project");
            Assert.That(manifest.recordCount, Is.GreaterThan(0));

            File.Delete(FileOf("IAdvCombat"));

            Assert.That(manifest.recordCount, Is.GreaterThan(0),
                "the manifest still cheerfully reports records that are no longer on disk");

            var load = BlobchegTransport.Default.Read(AdvCombatDb.FileName, Allocator.Persistent);
            try
            {
                Assert.Throws<BlobchegTransientException>(() => load.Complete(),
                    "but the load is obliged to fail explicitly — otherwise the drift between manifest and file would go into runtime");
            }
            finally
            {
                load.Dispose();
            }
        }

        [Test]
        public void A_returned_record_is_copied_and_the_base_is_not_spoiled()
        {
            var node = Node<AdvComboNodeSo>("Combo");
            node.rpm = 600;
            Dirty(node);
            Rebuild();

            var offset = OffsetOf(node, "IAdvCombat");
            var db = Combat();
            try
            {
                // A habit from the managed world: "took the object, fixed a field". Here that is a copy.
                var mine = db.Read<AdvGun>(offset);
                mine.Rpm = 1;
                mine.Ammo = -1f;

                Assert.That(db.Read<AdvGun>(offset).Rpm, Is.EqualTo(600),
                    "an edit of a copy has no right to reach the base — everyone reads it at once");
                Assert.That(db.Read<AdvGun>(offset).Ammo, Is.EqualTo(30f));
            }
            finally
            {
                db.Dispose();
            }
        }

        [Test]
        public void Editing_an_assembled_file_by_hand_is_rejected()
        {
            var node = Node<AdvComboNodeSo>("Combo");
            node.rpm = 600;
            Dirty(node);
            Rebuild();

            // "It is just data" — a developer edits a value straight in the binary.
            var offset = (int)OffsetOf(node, "IAdvCombat");
            var file = Bytes("IAdvCombat");
            BlobchegBytes.WriteU32(file, offset + 4, 1234);

            var buffer = BlobchegBuffer.From(file, Allocator.Persistent);
            try
            {
                Assert.Throws<InvalidOperationException>(() => { _ = new AdvCombatDb(buffer); },
                    "the file is derived and not a source; an edit past the rebuild is obliged to be visible at once");
            }
            finally
            {
                buffer.Dispose();
            }
        }

        [Test]
        public void An_edit_of_a_value_is_visible_in_the_rebuild_report()
        {
            var node = Node<AdvComboNodeSo>("Combo");
            node.rpm = 1;
            Dirty(node);
            Rebuild();

            node.rpm = 2;
            Dirty(node);
            var report = Rebuild();

            Assert.That(report.Changed, Is.True, "a value changed — the rebuild is obliged to notice that");
            Assert.That(report.ChangedFiles, Is.GreaterThan(0), "and to rewrite the file");

            var quiet = Rebuild();
            Assert.That(quiet.Changed, Is.False, "and after that — to touch nothing");
        }

        [Test]
        public void Deleting_the_last_node_clears_the_base_instead_of_leaving_yesterdays_one()
        {
            var node = Node<AdvComboNodeSo>("Combo");
            Rebuild();

            Assert.That(BlobchegBuild.RefsOf(node).Count(), Is.EqualTo(2), "the node wrote into two bases");
            var before = Bytes("IAdvCombat").Length;

            Kill(node);
            Rebuild();

            Assert.That(File.Exists(FileOf("IAdvCombat")), Is.True);

            var db = Combat();
            try
            {
                Assert.That(db.Length, Is.LessThan(before));
                Assert.That(db.Length, Is.EqualTo(BlobchegFormat.HeaderSize),
                    "no nodes are left — the base is obliged to become empty and not to stay yesterday's");
            }
            finally
            {
                db.Dispose();
            }
        }

        [Test]
        public void A_field_pointing_at_a_deleted_node_answers_with_an_error_and_not_a_zero_id()
        {
            var node = Node<AdvColdOnlyNodeSo>("Doomed");
            Rebuild();

            var field = new BlobchegIdRef<AdvRouter>(
                BlobchegBuild.IdsOf(node).Single(c => c.RouterName == AdvRouter.RouterName));

            Assert.That(field.IsSet, Is.True);
            Assert.That(field.Id.Index, Is.EqualTo(0u));

            Kill(node);

            Assert.That(field.IsSet, Is.False,
                "the asset was destroyed — the field is obliged to learn that by Unity's comparison and not by ReferenceEquals");
            Assert.Throws<InvalidOperationException>(() => { _ = field.Id; },
                "a dangling reference is obliged to fail and not to hand out id zero");
        }
    }
}

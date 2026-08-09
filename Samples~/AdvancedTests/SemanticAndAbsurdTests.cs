using System;
using System.IO;
using System.Linq;
using Blobcheg.Authoring;
using NUnit.Framework;
using UnityEditor;
using UnityEngine.TestTools;

namespace Blobcheg.AdvancedTests
{
    /// <summary>
    /// Use against the intended purpose and outright absurdity: two facades over one file, records
    /// referencing each other in a circle, a node referencing itself, an assembled blob slipped back in
    /// as a source, and a record with a raw pointer inside.
    ///
    /// The absurd scenarios here are not for laughs: they uncover what was assumed silently.
    /// </summary>
    public sealed class SemanticAndAbsurdTests : AdvancedFixture
    {
        [Test]
        public void Two_facades_over_one_domain_read_the_same_thing()
        {
            var node = Node<AdvLooseNodeSo>("Loose");
            node.a = 111;
            node.b = 222;
            Dirty(node);
            Rebuild();

            Assert.That(AdvLooseTwinDb.FileName, Is.EqualTo(AdvLooseDb.FileName),
                "two bases over one domain are one and the same file");

            var offset = OffsetOf(node, "IAdvLoose");
            var first = Loose();
            var second = new AdvLooseTwinDb(BufferOf(AdvLooseTwinDb.FileName));
            try
            {
                Assert.That(first.Read<AdvLooseBlock>(offset).A, Is.EqualTo(111));
                Assert.That(second.Read<AdvLooseBlock>(offset).A, Is.EqualTo(111),
                    "either the second facade is forbidden or it is obliged to read exactly the same");
                Assert.That(second.Read<AdvLooseBlock>(offset).B, Is.EqualTo(222));
            }
            finally
            {
                first.Dispose();
                second.Dispose();
            }
        }

        [Test]
        public void A_domain_outside_a_router_lives_without_ids()
        {
            var loose = Node<AdvLooseNodeSo>("Loose");
            var combo = Node<AdvComboNodeSo>("Combo");
            Rebuild();

            Assert.That(BlobchegBuild.IdsOf(loose).Count(), Is.Zero,
                "the base joined no router — its nodes never have an id at all");
            Assert.That(BlobchegBuild.RefsOf(loose).Count(), Is.EqualTo(1), "while the offset is there — it is the only address");

            Assert.That(BlobchegBuild.IdsOf(combo).Count(), Is.EqualTo(1), "a node of a router carries exactly one id");
        }

        [Test]
        public void Two_routers_side_by_side_do_not_confuse_the_bits()
        {
            var combo = Node<AdvComboNodeSo>("Combo");
            var other = Node<AdvOtherNodeSo>("Other");
            other.v = 4242;
            Dirty(other);
            Rebuild();

            var mainRouter = Router();
            var otherRouter = OtherRouter();
            var otherDb = Other();
            try
            {
                Assert.That(mainRouter.Count, Is.EqualTo(1), "only its own node joined the main router");
                Assert.That(otherRouter.Count, Is.EqualTo(1));

                var mine = mainRouter.Get(IdOf(combo, AdvRouter.RouterName));
                Assert.That(mine.HasCombat, Is.True);
                Assert.That(mine.HasCold, Is.True);

                var alien = otherRouter.Get(IdOf(other, AdvAlienRouter.RouterName));
                Assert.That(alien.HasOther, Is.True);
                Assert.That(otherDb.Read<AdvOtherInfo>(alien.other).V, Is.EqualTo(4242));
            }
            finally
            {
                mainRouter.Dispose();
                otherRouter.Dispose();
                otherDb.Dispose();
            }
        }

        [Test]
        public void An_assembled_blob_slipped_in_as_a_source_does_not_break_the_rebuild()
        {
            Node<AdvComboNodeSo>("Combo");
            Rebuild();

            // Absurd: we take the output of the pipeline and put it on the input, pretending it is an
            // asset. Importing binary garbage disguised as an .asset is a lawful reason for a console
            // error: it is expected here, and it is silenced in the test itself because the framework
            // resets the flag after SetUp.
            LogAssert.ignoreFailingMessages = true;

            var built = Bytes("IAdvCombat");
            File.WriteAllBytes(Folder + "/Impostor.asset", built);
            AssetDatabase.Refresh();

            Assert.DoesNotThrow(() => Rebuild(),
                "a foreign .asset in the project has no right either to break the rebuild or to be taken for a node");

            var db = Combat();
            try
            {
                Assert.That(db.IsCreated, Is.True, "and the base is obliged to stay working afterwards");
            }
            finally
            {
                db.Dispose();
            }
        }

        [Test]
        public void Records_reference_each_other_in_a_circle()
        {
            var a = Node<AdvComboNodeSo>("CycleA");
            var b = Node<AdvComboNodeSo>("CycleB");
            a.link = b;
            b.link = a;
            Dirty(a);
            Dirty(b);

            Rebuild();

            var idA = IdOf(a, AdvRouter.RouterName);
            var idB = IdOf(b, AdvRouter.RouterName);

            var router = Router();
            var cold = Cold();
            try
            {
                // We walk the circle by hand, with a cap: the package neither forbids a cycle nor is
                // obliged to, but it must be possible to walk it, and it is obliged to close.
                var at = idA;
                for (var hop = 0; hop < 4; hop++)
                {
                    var link = cold.Read<AdvColdInfo>(router.GetCold(at)).LinkId;
                    Assert.That(link, Is.Not.EqualTo(BlobchegId.NoneValue), $"step {hop} lost the reference");
                    at = new BlobchegId(link);
                }

                Assert.That(at, Is.EqualTo(idA), "an even number of steps around a circle of two is obliged to return to the start");
                Assert.That(cold.Read<AdvColdInfo>(router.GetCold(idA)).LinkId, Is.EqualTo(idB.Value));
                Assert.That(cold.Read<AdvColdInfo>(router.GetCold(idB)).LinkId, Is.EqualTo(idA.Value));
            }
            finally
            {
                router.Dispose();
                cold.Dispose();
            }
        }

        [Test]
        public void A_node_referencing_itself_gets_assembled()
        {
            var node = Node<AdvComboNodeSo>("Ouroboros");
            node.link = node;
            Dirty(node);

            Rebuild();

            var id = IdOf(node, AdvRouter.RouterName);

            var router = Router();
            var cold = Cold();
            try
            {
                ref readonly var record = ref cold.Read<AdvColdInfo>(router.GetCold(id));
                Assert.That(record.SelfId, Is.EqualTo(id.Value));
                Assert.That(record.LinkId, Is.EqualTo(id.Value),
                    "a reference to itself is the same id and neither recursion nor a refusal");
            }
            finally
            {
                router.Dispose();
                cold.Dispose();
            }
        }

        [Test]
        public void An_own_id_in_a_record_stays_correct_after_a_reshuffle()
        {
            var created = new[]
            {
                Node<AdvColdOnlyNodeSo>("R1"),
                Node<AdvColdOnlyNodeSo>("R2"),
                Node<AdvColdOnlyNodeSo>("R3"),
            };

            Rebuild();

            var byId = created.OrderBy(n => IdOf(n, AdvRouter.RouterName).Value).ToArray();
            Kill(byId[0]);
            Rebuild();

            var router = Router();
            var cold = Cold();
            try
            {
                foreach (var node in new[] { byId[1], byId[2] })
                {
                    var id = IdOf(node, AdvRouter.RouterName);
                    Assert.That(cold.Read<AdvColdInfo>(router.GetCold(id)).SelfId, Is.EqualTo(id.Value),
                        $"node '{node.name}' put its own id into the record — after the reshuffle it is obliged to agree");
                }
            }
            finally
            {
                router.Dispose();
                cold.Dispose();
            }
        }

        /// <summary>
        /// The <c>where T : unmanaged</c> constraint only answers for "there are no managed references":
        /// it lets a struct with a <c>byte*</c> or <c>IntPtr</c> field through because that struct is
        /// formally unmanaged. It is rejected by a separate check in the pipeline — and it is the
        /// pipeline that is obliged to reject it: an address outlives the write but not a restart of the
        /// process, and on a read it hands out garbage indistinguishable from a value.
        /// </summary>
        [Test]
        public void A_record_with_a_raw_pointer_is_rejected()
        {
            var node = Node<AdvPointerNodeSo>("Pointer");

            var thrown = Assert.Throws<InvalidOperationException>(() => Rebuild(),
                "a pointer in a file is not data; such a record is obliged to be rejected by the pipeline and not by the consumer");
            StringAssert.Contains("Ptr", thrown.Message,
                "and to name the field: hunting for it by eye in a fat struct is not a human's job");

            Kill(node);
        }

        [Test]
        public void A_pointer_deep_inside_a_record_is_rejected_too()
        {
            var node = Node<AdvNestedPointerNodeSo>("Nested");

            var thrown = Assert.Throws<InvalidOperationException>(() => Rebuild(),
                "a pointer hidden in a struct field is no better than a pointer in plain sight");
            StringAssert.Contains(nameof(AdvPointerHolder.Handle), thrown.Message);

            Kill(node);
        }

        [Test]
        public void A_record_without_a_single_field_is_addressable()
        {
            var one = Node<AdvEmptyRecordNodeSo>("EmptyA");
            var two = Node<AdvEmptyRecordNodeSo>("EmptyB");
            Rebuild();

            var first = OffsetOf(one, "IAdvLoose");
            var second = OffsetOf(two, "IAdvLoose");

            Assert.That(first, Is.Not.EqualTo(second),
                "a record without fields still has a size, and two addresses cannot coincide");

            var db = Loose();
            try
            {
                Assert.DoesNotThrow(() => { Copy(db.Read<AdvEmptyRecord>(first)); });
                Assert.DoesNotThrow(() => { Copy(db.Read<AdvEmptyRecord>(second)); });
            }
            finally
            {
                db.Dispose();
            }
        }
    }
}

using System;
using System.IO;
using NUnit.Framework;

namespace Blobcheg.AdvancedTests
{
    /// <summary>
    /// Arrays inside a record: the abuses from PLAN-arrays.md. All of it through the editor cycle, via
    /// nodes and the rebuild: the builder is not handed to a consumer outside the pipeline.
    /// </summary>
    public sealed class ArrayDestructiveTests : AdvancedFixture
    {
        [Test]
        public void An_array_of_a_million_elements_is_assembled_and_read()
        {
            var node = Node<AdvWeightsNodeSo>("Huge");
            node.count = 1_000_000;
            Dirty(node);
            Rebuild();

            var db = Loose();
            try
            {
                ref readonly var record = ref db.Read<AdvWeights>(OffsetOf(node, "IAdvLoose"));
                Assert.That(record.Weights.Length, Is.EqualTo(1_000_000));
                Assert.That(record.Weights[0], Is.EqualTo(0f));
                Assert.That(record.Weights[999_999], Is.EqualTo(999_999 * 0.5f),
                    "the last element is obliged to survive into the file and come back");
            }
            finally
            {
                db.Dispose();
            }
        }

        [Test]
        public void A_forgotten_Allocate_reads_as_emptiness_and_not_as_garbage()
        {
            var node = Node<AdvForgottenAllocateNodeSo>("Forgotten");
            Rebuild();

            var db = Loose();
            try
            {
                ref readonly var record = ref db.Read<AdvWeights>(OffsetOf(node, "IAdvLoose"));
                Assert.That(record.Rolls, Is.EqualTo(9), "the filled head fields arrived");
                Assert.That(record.Weights.IsEmpty, Is.True, "an unfilled array field is emptiness");
                Assert.That(record.Weights.Length, Is.Zero);
            }
            finally
            {
                db.Dispose();
            }
        }

        [Test]
        public void A_literal_with_an_array_at_depth_is_rejected()
        {
            Node<AdvArrayLiteralNodeSo>("DeepLiteral");

            var thrown = Assert.Throws<InvalidOperationException>(() => Rebuild());
            StringAssert.Contains("Begin", thrown.Message,
                "the refusal is obliged to name the right form even when the array is hidden at the second level");
        }

        [Test]
        public void An_array_window_after_End_throws_instead_of_writing_into_freed_memory()
        {
            Node<AdvLateWindowNodeSo>("LateWindow");

            var thrown = Assert.Throws<InvalidOperationException>(() => Rebuild());
            StringAssert.Contains("End", thrown.Message);
            StringAssert.Contains("LateWindow", thrown.Message, "the error is obliged to name the node");
        }

        [Test]
        public void A_Write_that_failed_in_the_middle_of_an_array_delivers_its_own_error()
        {
            var node = Node<AdvThrowingBuilderNodeSo>("Thrower");

            var thrown = Assert.Throws<InvalidOperationException>(() => Rebuild());
            StringAssert.Contains(AdvThrowingBuilderNodeSo.Cry, thrown.Message,
                "what is obliged to reach a human is the node's error and not a complaint about an unclosed builder");

            // The state corruption did not outlive the failure: without the culprit the rebuild is alive again.
            Kill(node);
            Assert.DoesNotThrow(() => Rebuild());
        }

        [Test]
        public void A_field_of_a_foreign_builder_is_rejected()
        {
            Node<AdvCrossBuilderNodeSo>("Cross");

            var thrown = Assert.Throws<InvalidOperationException>(() => Rebuild());
            StringAssert.Contains("not from this record", thrown.Message);
        }

        [Test]
        public void A_tree_over_a_recursive_element_type_is_built_and_read()
        {
            var node = Node<AdvTreeNodeSo>("Tree");
            Rebuild();

            var db = Loose();
            try
            {
                ref readonly var tree = ref db.Read<AdvTree>(OffsetOf(node, "IAdvLoose"));
                Assert.That(tree.Roots.Length, Is.EqualTo(2));
                Assert.That(tree.Roots[0].Value, Is.EqualTo(1));
                Assert.That(tree.Roots[1].Value, Is.EqualTo(2));
                Assert.That(tree.Roots[1].Children.IsEmpty, Is.True, "a leaf without an Allocate is emptiness");
                Assert.That(tree.Roots[0].Children.Length, Is.EqualTo(2));
                Assert.That(tree.Roots[0].Children[0].Value, Is.EqualTo(11));
                Assert.That(tree.Roots[0].Children[1].Value, Is.EqualTo(12));
                Assert.That(tree.Roots[0].Children[1].Children[0].Value, Is.EqualTo(121),
                    "the third level — the offset is measured from the field of its own element");
            }
            finally
            {
                db.Dispose();
            }
        }

        [Test]
        public void Ten_length_edits_hold_the_file_and_other_addresses()
        {
            var neighbour = Node<AdvLooseNodeSo>("Neighbour");
            var victim = Node<AdvWeightsNodeSo>("Victim");
            Dirty(victim);
            Rebuild();
            var neighbourAt = OffsetOf(neighbour, "IAdvLoose");

            var lengths = new long[10];
            for (var edit = 0; edit < 10; edit++)
            {
                victim.count = edit % 2 == 0 ? 40 : 3;
                Dirty(victim);
                Rebuild();

                Assert.That(OffsetOf(neighbour, "IAdvLoose"), Is.EqualTo(neighbourAt),
                    "editing someone's length has no right to move the neighbour");
                lengths[edit] = new FileInfo(FileOf("IAdvLoose")).Length;
            }

            for (var i = 4; i < lengths.Length; i++)
                Assert.That(lengths[i], Is.EqualTo(lengths[i - 2]),
                    "the file is obliged to settle into a stable cycle rather than grow with every edit");
        }
    }
}

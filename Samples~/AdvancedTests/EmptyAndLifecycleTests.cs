using System;
using System.IO;
using NUnit.Framework;
using Unity.Collections;

namespace Blobcheg.AdvancedTests
{
    /// <summary>
    /// Emptiness, zero and the order of calls: a base without nodes, a record without bytes, a file that
    /// is not there, a read before the load and after the free, and every way to lie in a node's
    /// declaration.
    /// </summary>
    public sealed class EmptyAndLifecycleTests : AdvancedFixture
    {
        [Test]
        public void A_domain_without_a_single_node_lands_as_a_file_and_any_read_fails()
        {
            Node<AdvComboNodeSo>("Combo");
            Rebuild();

            Assert.That(File.Exists(FileOf("IAdvOther")), Is.True,
                "a domain whose last node left is obliged to land as an empty file and not to stay yesterday's one");

            var db = Other();
            try
            {
                Assert.That(db.IsCreated, Is.True);
                Assert.That(db.Length, Is.EqualTo(BlobchegFormat.HeaderSize), "an empty base holds nothing but the header");
                Assert.Throws<InvalidOperationException>(
                    () => { _ = db.Read<AdvOtherInfo>(BlobchegFormat.HeaderSize).V; },
                    "an empty base holds not a single record — the read is obliged to fail and not to hand out zeroes");
            }
            finally
            {
                db.Dispose();
            }
        }

        /// <summary>
        /// The address is the only identity a record has in this format, which is why a record of zero
        /// length takes a byte in the layout and not zero. Otherwise the position after it would not
        /// move, the next alignment would return the same address, and two different nodes would get one
        /// ref asset.
        /// </summary>
        [Test]
        public void Two_empty_raw_records_are_obliged_to_have_different_addresses()
        {
            var a = Node<AdvRawNodeSo>("RawEmptyA");
            var b = Node<AdvRawNodeSo>("RawEmptyB");
            a.size = 0;
            b.size = 0;
            Dirty(a);
            Dirty(b);

            Rebuild();

            Assert.That(OffsetOf(a, "IAdvLoose"), Is.Not.EqualTo(OffsetOf(b, "IAdvLoose")),
                "the address is the only identity a record has; two records at one address are indistinguishable");
        }

        [Test]
        public void A_record_of_zero_length_is_not_obliged_to_read_as_a_struct()
        {
            var raw = Node<AdvRawNodeSo>("RawEmpty");
            var loose = Node<AdvLooseNodeSo>("Loose");
            raw.size = 0;
            Dirty(raw);
            Dirty(loose);

            Rebuild();

            var offset = OffsetOf(raw, "IAdvLoose");
            var db = Loose();
            try
            {
                Assert.Throws<InvalidOperationException>(
                    () => { _ = db.Read<AdvLooseBlock>(offset).A; },
                    "the record holds zero bytes — a 16-byte struct cannot come out of it");
            }
            finally
            {
                db.Dispose();
            }
        }

        [Test]
        public void A_missing_base_file_fails_explicitly_on_load()
        {
            Node<AdvComboNodeSo>("Combo");
            Rebuild();

            File.Delete(FileOf("IAdvCombat"));

            var load = BlobchegTransport.Default.Read(AdvCombatDb.FileName, Allocator.Persistent);
            try
            {
                // A transient type and not an ordinary one: on a live project "there is no file" also means
                // "not yet" — the domain arrived with a pull before the rebuild wrote its file.
                Assert.Throws<BlobchegTransientException>(() => load.Complete(),
                    "there is no file — that is a load error and not an empty base");
            }
            finally
            {
                load.Dispose();
            }
        }

        [Test]
        public void A_file_of_zero_length_fails_on_load()
        {
            Node<AdvComboNodeSo>("Combo");
            Rebuild();

            File.WriteAllBytes(FileOf("IAdvCombat"), Array.Empty<byte>());

            var load = BlobchegTransport.Default.Read(AdvCombatDb.FileName, Allocator.Persistent);
            try
            {
                Assert.Throws<InvalidOperationException>(() => load.Complete(),
                    "a zero-length file is shorter than the header — there is nothing to load");
            }
            finally
            {
                load.Dispose();
            }
        }

        [Test]
        public void Acquire_before_readiness_fails()
        {
            Node<AdvComboNodeSo>("Combo");
            Rebuild();

            var load = BlobchegTransport.Default.Read(AdvCombatDb.FileName, Allocator.Persistent);
            try
            {
                Assert.Throws<InvalidOperationException>(() => load.Acquire(),
                    "the buffer is taken after readiness and not instead of waiting");
            }
            finally
            {
                load.Dispose();
            }
        }

        [Test]
        public void Reading_from_a_base_that_is_not_loaded_fails()
        {
            var db = default(AdvCombatDb);

            Assert.That(db.IsCreated, Is.False);
            Assert.Throws<InvalidOperationException>(
                () => { _ = db.Read<AdvGun>(BlobchegFormat.HeaderSize).Rpm; },
                "the base is not loaded — the read is obliged to fail and not to walk address zero");
        }

        [Test]
        public void Reading_after_Dispose_fails()
        {
            var node = Node<AdvComboNodeSo>("Combo");
            Rebuild();

            var offset = OffsetOf(node, "IAdvCombat");
            var db = Combat();
            Assert.That(db.Read<AdvGun>(offset).Rpm, Is.EqualTo(600), "before the free it reads");

            db.Dispose();

            Assert.Throws<InvalidOperationException>(
                () => { _ = db.Read<AdvGun>(offset).Rpm; },
                "a freed base is obliged to fail and not to read freed memory");
        }

        [Test]
        public void A_repeated_Dispose_breaks_nothing()
        {
            Node<AdvComboNodeSo>("Combo");
            Rebuild();

            var db = Combat();
            db.Dispose();

            Assert.DoesNotThrow(() => db.Dispose(), "a second Dispose is an idempotent no-op and not a double free");
            Assert.That(db.IsCreated, Is.False);
        }

        /// <summary>
        /// AN ACCEPTED LIMIT, not a finding. A base is a value struct with an owning pointer, and it was
        /// made that way on purpose: it is put into an <c>IComponentData</c> and copied by every
        /// <c>GetSingleton</c>, by every hand-off into a job, by every assignment. An ownership version
        /// (a safety handle, as in NativeArray) requires a cell that outlives the freeing of the memory
        /// itself — that is, either a leak or a registry unavailable from Burst. Neither fits into a
        /// component field.
        ///
        /// Hence the plain contract: a base has one owner — whoever loaded it (with Entities that is the
        /// boot system emitted by the codegen). The other instances are views living exactly as long as
        /// the owner does. The test pins that contract down so that it does not look like an oversight.
        /// </summary>
        [Test]
        public void A_copy_of_a_base_is_a_view_and_not_an_owner()
        {
            Node<AdvComboNodeSo>("Combo");
            Rebuild();

            var db = Combat();
            var copy = db;

            Assert.That(copy.IsCreated, Is.True, "while the owner is alive the view works like the owner itself");
            Assert.That(copy.Length, Is.EqualTo(db.Length));

            db.Dispose();

            Assert.That(db.IsCreated, Is.False, "the owner knows about its own death");
            Assert.That(copy.IsCreated, Is.True,
                "while the view does not: a pointer in an ordinary struct has no ownership version. " +
                "Hence the rule of the package: Dispose is called by whoever loaded it, exactly once");

            // Neither reading through copy nor calling Dispose on it is ALLOWED here: the first is a read
            // of freed memory, the second a double free. Both would crash the editor together with the
            // report, and what has to be shown is exactly what is already shown.
        }

        [Test]
        public void A_rebuild_over_a_live_handle_does_not_swap_the_data()
        {
            var node = Node<AdvComboNodeSo>("Combo");
            node.rpm = 111;
            Dirty(node);
            Rebuild();

            var offset = OffsetOf(node, "IAdvCombat");
            var db = Combat();
            try
            {
                node.rpm = 222;
                Dirty(node);
                Rebuild();

                Assert.That(db.Read<AdvGun>(offset).Rpm, Is.EqualTo(111),
                    "a loaded base is a snapshot; rebuilding the file on disk has no right to stir someone else's buffer");
            }
            finally
            {
                db.Dispose();
            }
        }

        [Test]
        public void A_node_without_a_single_domain_fails()
        {
            var node = Node<AdvNoOutTypesNodeSo>("NoOut");

            var thrown = Assert.Throws<InvalidOperationException>(() => Rebuild(),
                "a node without OutTypes means nothing — that is an error and not a quiet skip");
            StringAssert.Contains("OutTypes", thrown.Message);

            Kill(node);
        }

        [Test]
        public void A_node_with_an_undeclared_domain_fails()
        {
            var node = Node<AdvUndeclaredNodeSo>("Undeclared");

            Assert.Throws<InvalidOperationException>(() => Rebuild(),
                "a domain without a base is not a domain; there is nobody to read such a file");

            Kill(node);
        }

        [Test]
        public void A_node_that_declared_a_domain_and_wrote_nothing_fails()
        {
            var node = Node<AdvSilentNodeSo>("Silent");

            var thrown = Assert.Throws<InvalidOperationException>(() => Rebuild(),
                "it declared and did not write — a disagreement between the declaration and the fact");
            StringAssert.Contains("OutTypes", thrown.Message);

            Kill(node);
        }

        [Test]
        public void A_node_writing_past_its_own_OutTypes_fails()
        {
            var node = Node<AdvStrayNodeSo>("Stray");

            Assert.Throws<InvalidOperationException>(() => Rebuild(),
                "a write into a domain the node never declared is a hole in the handing out of ids");

            Kill(node);
        }

        [Test]
        public void A_node_writing_into_one_domain_twice_fails()
        {
            var node = Node<AdvDoubleNodeSo>("Double");

            Assert.Throws<InvalidOperationException>(() => Rebuild(),
                "one node means one record in a base; otherwise a node has two addresses and an id stops being an address");

            Kill(node);
        }

        [Test]
        public void A_failing_node_does_not_leave_a_half_assembled_file()
        {
            var good = Node<AdvComboNodeSo>("Good");
            good.rpm = 900;
            Dirty(good);
            Rebuild();

            var before = Bytes("IAdvCombat");

            var bad = Node<AdvThrowNodeSo>("Boom");
            Assert.Throws<InvalidOperationException>(() => Rebuild());

            Assert.That(Bytes("IAdvCombat"), Is.EqualTo(before),
                "the build either went through whole or did not touch the file: a half-assembled base looks alive and lies");

            Kill(bad);
        }

        [Test]
        public void The_rebuild_is_idempotent_on_a_mixed_set()
        {
            Node<AdvComboNodeSo>("Combo");
            Node<AdvColdOnlyNodeSo>("Cold");
            Node<AdvArmorNodeSo>("Armor");
            Node<AdvLooseNodeSo>("Loose");
            Node<AdvOtherNodeSo>("Other");
            var raw = Node<AdvRawNodeSo>("Raw");
            raw.size = 24;
            Dirty(raw);

            Rebuild();

            var again = Rebuild();
            Assert.That(again.Changed, Is.False,
                $"the second rebuild has no right to touch anything, report: {again}");
        }
    }
}

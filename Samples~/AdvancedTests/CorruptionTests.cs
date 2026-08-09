using System;
using NUnit.Framework;
using Unity.Collections;

namespace Blobcheg.AdvancedTests
{
    /// <summary>
    /// Corrupting the file. Integrity is the only check of the package that ALWAYS works, behind no
    /// define and in a build; here it is broken in every way a file on disk breaks: truncated, extended,
    /// a byte flipped, the header swapped, a lie in the router prolog.
    ///
    /// Some of the tests re-stamp the header (<c>Reseal</c>) on purpose: without it the content hash
    /// would fire first and something other than what was meant would be checked.
    /// </summary>
    public sealed class CorruptionTests : AdvancedFixture
    {
        void Baked()
        {
            var node = Node<AdvComboNodeSo>("Combo");
            node.rpm = 555;
            Dirty(node);
            Node<AdvColdOnlyNodeSo>("Cold");
            Node<AdvArmorNodeSo>("Armor");
            Rebuild();
        }

        static void RefusedAsBase(byte[] file, string because)
        {
            var buffer = BlobchegBuffer.From(file, Allocator.Persistent);
            try
            {
                Assert.Throws<InvalidOperationException>(() => { _ = new AdvCombatDb(buffer); }, because);
            }
            finally
            {
                // It frees exactly once in both outcomes: a constructor that threw did not take ownership,
                // and a constructor that went through sits on this same memory.
                buffer.Dispose();
            }
        }

        /// <summary>
        /// A failure with an expiry date. A disagreement with the length from the header is the only
        /// corruption that on a live disk means not "broken" but "still being written": the reader learns
        /// the length before the body, and between those two reads a rebuild has time to swap the file.
        /// The failure type is obliged to tell those apart — the whole difference between a warning and
        /// red in the editor stands on it.
        /// </summary>
        static void RefusedAsBaseTransiently(byte[] file, string because)
        {
            var buffer = BlobchegBuffer.From(file, Allocator.Persistent);
            try
            {
                Assert.Throws<BlobchegTransientException>(() => { _ = new AdvCombatDb(buffer); }, because);
            }
            finally
            {
                // It frees exactly once in both outcomes: a constructor that threw did not take ownership,
                // and a constructor that went through sits on this same memory.
                buffer.Dispose();
            }
        }

        static void RefusedAsRouter(byte[] file, string because)
        {
            var buffer = BlobchegBuffer.From(file, Allocator.Persistent);
            try
            {
                Assert.Throws<InvalidOperationException>(() => { _ = new AdvRouter(buffer); }, because);
            }
            finally
            {
                buffer.Dispose();
            }
        }

        [Test]
        public void A_file_truncated_by_a_byte_does_not_load()
        {
            Baked();

            var file = Bytes("IAdvCombat");
            var cut = new byte[file.Length - 1];
            Array.Copy(file, cut, cut.Length);

            RefusedAsBaseTransiently(cut, "the file length is written in the header — truncation is visible at once");
        }

        [Test]
        public void An_appended_tail_does_not_load()
        {
            Baked();

            var file = Bytes("IAdvCombat");
            var grown = new byte[file.Length + 16];
            Array.Copy(file, grown, file.Length);

            RefusedAsBaseTransiently(grown, "an appended tail is also a disagreement with the length from the header");
        }

        [Test]
        public void A_flipped_byte_in_the_body_does_not_load()
        {
            Baked();

            var file = Bytes("IAdvCombat");
            file[BlobchegFormat.HeaderSize + 2] ^= 0xFF;

            RefusedAsBase(file, "the integrity is computed over the whole body and is obliged to catch one byte");
        }

        [Test]
        public void A_flipped_bit_in_the_body_does_not_load()
        {
            Baked();

            var file = Bytes("IAdvCombat");
            file[file.Length - 1] ^= 0x01;

            RefusedAsBase(file, "one bit in the last byte is the same case as the whole byte");
        }

        [Test]
        public void A_corrupted_magic_does_not_load()
        {
            Baked();

            // The header must not be re-stamped here: Seal would put the magic back. The content hash is
            // computed over the body of the file only, so editing the header does not touch it.
            var file = Bytes("IAdvCombat");
            file[0] ^= 0xFF;

            RefusedAsBase(file, "a non-blobcheg file is an error and not an attempt to read whatever comes out");
        }

        [Test]
        public void A_foreign_format_version_does_not_load()
        {
            Baked();

            var file = Bytes("IAdvCombat");
            BlobchegBytes.WriteU16(file, 4, (ushort)(BlobchegFormat.Version + 7));

            RefusedAsBase(file, "the reader does not understand a foreign version and has no right to guess");
        }

        [Test]
        public void Random_garbage_of_the_right_length_does_not_load()
        {
            Baked();

            var file = Bytes("IAdvCombat");
            var garbage = new byte[file.Length];
            var random = new System.Random(20260728);
            random.NextBytes(garbage);

            RefusedAsBase(garbage, "garbage is obliged to be rejected rather than crash the process by reading anywhere");
        }

        [Test]
        public void A_router_file_does_not_load_as_a_base()
        {
            Baked();

            RefusedAsBase(Bytes("AdvRouter"),
                "mixed-up files are the cheapest way to read one thing instead of another");
        }

        [Test]
        public void A_base_file_does_not_load_as_a_router()
        {
            Baked();

            RefusedAsRouter(Bytes("IAdvCombat"), "and the other way round too");
        }

        [Test]
        public void The_router_flag_in_a_base_file_does_not_load()
        {
            Baked();

            var file = Bytes("IAdvCombat");
            BlobchegBytes.WriteU16(file, 6, (ushort)(BlobchegBytes.ReadU16(file, 6) | BlobchegFormat.FlagRouter));

            RefusedAsBase(file, "the flag lied about the kind of the file — that is a load error");
        }

        [Test]
        public void A_debug_section_past_the_end_of_the_file_does_not_load()
        {
            Baked();

            var file = Bytes("IAdvCombat");
            BlobchegBytes.WriteU32(file, 12, (uint)file.Length);

            RefusedAsBase(file, "the offset of the debug section is obliged to lie inside the file");
        }

        [Test]
        public void A_router_with_a_swapped_LayoutHash_does_not_load()
        {
            Baked();

            var file = Bytes("AdvRouter");
            var at = BlobchegRouterFormat.PrologOffset + 8;
            BlobchegBytes.WriteU64(file, at, BlobchegBytes.ReadU64(file, at) ^ 0xFFFFFFFFFFFFFFFFUL);
            Reseal(file);

            RefusedAsRouter(file,
                "the bit numbering in the file and in the codegen diverged — reading such a router means reading the wrong base");
        }

        [Test]
        public void A_router_with_a_foreign_number_of_bases_does_not_load()
        {
            Baked();

            var file = Bytes("AdvRouter");
            BlobchegBytes.WriteU32(file, BlobchegRouterFormat.PrologOffset + 4, (uint)(AdvRouter.DomainCount + 1));
            Reseal(file);

            RefusedAsRouter(file, "the file holds one number of bases and the code another — the mask means different things");
        }

        [Test]
        public void A_router_with_an_inflated_row_count_does_not_load()
        {
            Baked();

            var file = Bytes("AdvRouter");
            BlobchegBytes.WriteU32(file, BlobchegRouterFormat.PrologOffset + 0, 100000);
            Reseal(file);

            RefusedAsRouter(file, "more rows than fit into the file — Get would send the read into foreign memory");
        }

        [Test]
        public void A_router_with_a_prolog_pointing_past_the_file_does_not_load()
        {
            Baked();

            var file = Bytes("AdvRouter");
            BlobchegBytes.WriteU32(file, BlobchegRouterFormat.PrologOffset + 16, (uint)file.Length + 1024);
            Reseal(file);

            RefusedAsRouter(file, "the mask array is pointed past the end of the file");
        }

        [Test]
        public void A_router_with_a_prolog_pointing_into_the_header_does_not_load()
        {
            Baked();

            var file = Bytes("AdvRouter");
            BlobchegBytes.WriteU32(file, BlobchegRouterFormat.PrologOffset + 16, 8);
            Reseal(file);

            RefusedAsRouter(file, "the masks cannot start inside the header");
        }

        [Test]
        public void A_whole_file_still_loads_after_the_header_is_re_stamped()
        {
            Baked();

            // A control over the test instrument itself: a Reseal without editing the body is obliged to
            // leave the file working. Otherwise every test above is green for the wrong reason.
            var file = Bytes("IAdvCombat");
            Reseal(file);

            var buffer = BlobchegBuffer.From(file, Allocator.Persistent);
            var db = new AdvCombatDb(buffer);
            try
            {
                Assert.That(db.IsCreated, Is.True);
            }
            finally
            {
                db.Dispose();
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Unity.Collections;

namespace Blobcheg.AdvancedTests
{
    /// <summary>
    /// Address boundaries and type boundaries: past the end of the file, into the header, off the
    /// alignment, exactly on the last record, exactly on 64 bases of a router — and what happens when a
    /// record is read with a type of the same size but the wrong one.
    /// </summary>
    public sealed class BoundaryAndTypeTests : AdvancedFixture
    {
        [Test]
        public void An_offset_past_the_end_of_the_file_fails()
        {
            var node = Node<AdvComboNodeSo>("Combo");
            Rebuild();

            var db = Combat();
            try
            {
                var past = (uint)BlobchegFormat.AlignUp(db.Length);

                Assert.Throws<InvalidOperationException>(() => { _ = db.Read<AdvGun>(past).Rpm; },
                    "an address exactly at the end of the file is no longer a record");

                Assert.Throws<InvalidOperationException>(() => { _ = db.Read<AdvGun>(past + 16u).Rpm; });
                Assert.Throws<InvalidOperationException>(() => { _ = db.Read<AdvGun>(0xFFFFFFF0u).Rpm; },
                    "an address near the uint ceiling has no right to fold into a valid one");

                Assert.That(db.Read<AdvGun>(OffsetOf(node, "IAdvCombat")).Rpm, Is.EqualTo(600),
                    "and a real address is obliged to read all the same");
            }
            finally
            {
                db.Dispose();
            }
        }

        [Test]
        public void An_offset_off_the_alignment_fails()
        {
            var node = Node<AdvComboNodeSo>("Combo");
            Rebuild();

            var offset = OffsetOf(node, "IAdvCombat");
            var db = Combat();
            try
            {
                Assert.Throws<InvalidOperationException>(() => { _ = db.Read<AdvGun>(offset + 1u).Rpm; },
                    "the start of a record is always a multiple of 16 — everything else is not the start of a record");
                Assert.Throws<InvalidOperationException>(() => { _ = db.Read<AdvGun>(offset + 15u).Rpm; });
            }
            finally
            {
                db.Dispose();
            }
        }

        [Test]
        public void An_offset_into_the_header_fails()
        {
            Node<AdvComboNodeSo>("Combo");
            Rebuild();

            var db = Combat();
            try
            {
                Assert.Throws<InvalidOperationException>(() => { _ = db.Read<AdvGun>(0).Rpm; },
                    "address zero is the header and not a record; it is also the value of an uninitialised field");
                Assert.Throws<InvalidOperationException>(() => { _ = db.Read<AdvGun>(16).Rpm; });
            }
            finally
            {
                db.Dispose();
            }
        }

        [Test]
        public void A_type_larger_than_the_record_does_not_fit_into_the_buffer()
        {
            var node = Node<AdvComboNodeSo>("Combo");
            Rebuild();

            var offset = OffsetOf(node, "IAdvCombat");
            var db = Combat();
            try
            {
                Assert.Throws<InvalidOperationException>(() => { _ = db.Read<AdvFat>(offset).C0.A; },
                    "a 512-byte struct cannot come out of an 8-byte record");
            }
            finally
            {
                db.Dispose();
            }
        }

        [Test]
        public void The_last_record_is_read_whole()
        {
            var fat = Node<AdvFatNodeSo>("Fat");
            fat.first = 3.25;
            fat.last = -7.75;
            Dirty(fat);
            Rebuild();

            var offset = OffsetOf(fat, "IAdvCombat");
            var db = Combat();
            try
            {
                Assert.That(offset + 512u, Is.LessThanOrEqualTo((uint)db.Length),
                    "a record is obliged to fit into the file whole");

                ref readonly var record = ref db.Read<AdvFat>(offset);
                Assert.That(record.C0.A, Is.EqualTo(3.25), "the first 8 bytes of the last record");
                Assert.That(record.C7.H, Is.EqualTo(-7.75), "and its last 8 bytes too — right up to the end of the file");
            }
            finally
            {
                db.Dispose();
            }
        }

        [Test]
        public void A_one_byte_record_does_not_shift_the_neighbours()
        {
            var tiny = Node<AdvRawNodeSo>("Tiny");
            var big = Node<AdvRawNodeSo>("Big");
            tiny.size = 1;
            tiny.seed = 0x11;
            big.size = 40;
            big.seed = 0x20;
            Dirty(tiny);
            Dirty(big);

            Rebuild();

            var tinyAt = OffsetOf(tiny, "IAdvLoose");
            var bigAt = OffsetOf(big, "IAdvLoose");

            Assert.That(tinyAt, Is.Not.EqualTo(bigAt));
            Assert.That(Math.Abs((long)tinyAt - bigAt), Is.GreaterThanOrEqualTo(16),
                "there is always alignment between the starts of two records");

            var file = Bytes("IAdvLoose");
            Assert.That(file[(int)tinyAt], Is.EqualTo((byte)0x11), "the one-byte record lies where its address promised");
            Assert.That(file[(int)bigAt], Is.EqualTo((byte)0x20));
            Assert.That(file[(int)bigAt + 39], Is.EqualTo((byte)(0x20 + 39)), "and the neighbour is not truncated");
        }

        [Test]
        public void A_router_without_a_single_base_is_rejected()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => BlobchegRouterFormat.MaskWidthFor(0),
                "a router without bases has nothing to route");

            Assert.Throws<ArgumentOutOfRangeException>(
                () => BlobchegRouterWriter.Open(Scratch, "AdvEmptyRouter", 0, 0),
                "the router writer is obliged to reject that at the input and not to assemble a file nobody will load");
        }

        [Test]
        public void More_than_sixty_four_bases_is_rejected()
        {
            Assert.Throws<ArgumentOutOfRangeException>(
                () => BlobchegRouterFormat.MaskWidthFor(BlobchegRouterFormat.MaxDomains + 1),
                "there is no mask wider than 64 bits — that is obliged to be an error and not a lost base");

            Assert.Throws<ArgumentOutOfRangeException>(
                () => BlobchegRouterWriter.Open(Scratch, "AdvWideRouter", BlobchegRouterFormat.MaxDomains + 1, 0));
        }

        [Test]
        public void A_router_on_exactly_sixty_four_bases_lives()
        {
            const int count = BlobchegRouterFormat.MaxDomains;

            var pairs = Enumerable.Range(0, count)
                .Select(i => new KeyValuePair<string, string>("Domain" + i.ToString("D2"), "member" + i))
                .ToList();

            var width = BlobchegRouterFormat.MaskWidthFor(count);
            var layout = BlobchegRouterFormat.LayoutHash(pairs, width);

            var writer = BlobchegRouterWriter.Open(Scratch, "Adv64Router", count, layout);
            writer.Append("edges", new[]
            {
                new BlobchegRouterCell(0, 0x100),
                new BlobchegRouterCell(count - 1, 0x200),
            });
            writer.Append("empty", Array.Empty<BlobchegRouterCell>());
            writer.Flush();

            var blob = new BlobchegRouterBlob(
                BlobchegBuffer.From(File.ReadAllBytes(writer.FilePath), Allocator.Persistent),
                "Adv64Router", count, layout);

            try
            {
                var edges = blob.Get(blob.IdAt(0));
                Assert.That(edges.Has(0), Is.True);
                Assert.That(edges.Has(count - 1), Is.True, "the top bit of the mask is the one popcount breaks on");
                Assert.That(edges.Offset(0), Is.EqualTo(0x100u));
                Assert.That(edges.Offset(count - 1), Is.EqualTo(0x200u));
                Assert.That(edges.Has(1), Is.False);

                var empty = blob.Get(blob.IdAt(1));
                Assert.That(empty.Mask, Is.EqualTo(0ul), "a row without a single base is allowed");
                Assert.Throws<InvalidOperationException>(() => empty.Offset(0),
                    "but it has no offset, and there can be no sentinel in its place");
                Assert.That(empty.TryOffset(0, out _), Is.False);
            }
            finally
            {
                blob.Dispose();
            }
        }

        [Test]
        public void An_id_past_the_last_row_fails()
        {
            Node<AdvComboNodeSo>("Combo");
            Node<AdvColdOnlyNodeSo>("Cold");
            Rebuild();

            var router = Router();
            try
            {
                Assert.That(router.Count, Is.EqualTo(2));

                Assert.Throws<InvalidOperationException>(() => router.Get(router.IdAt((uint)router.Count)),
                    "there is no row with that number — that is an error and not an empty row");
                Assert.Throws<InvalidOperationException>(() => router.Get(router.IdAt(BlobchegId.MaxIndex)));
                Assert.Throws<InvalidOperationException>(() => router.Get(new BlobchegId(uint.MaxValue - 1)));
                Assert.Throws<InvalidOperationException>(() => router.Get(BlobchegId.None));

                Assert.That(router.TryGet(router.IdAt((uint)router.Count), out _), Is.False);
                Assert.That(router.TryGet(BlobchegId.None, out _), Is.False);

                Assert.Throws<ArgumentOutOfRangeException>(() => router.IdAt(BlobchegId.MaxIndex + 1),
                    "a row past the router ceiling is not an id but garbage with a foreign tag inside");
            }
            finally
            {
                router.Dispose();
            }
        }

        /// <summary>
        /// The domain constraint in the generated <c>Read&lt;T&gt;</c> catches only a FOREIGN domain — a
        /// twin inside its own domain passes the compiler straight through. What catches it is the debug
        /// contour, and it lives under the same define as the bounds check: in the editor and in a
        /// development build.
        /// </summary>
        [Test]
        public void A_twin_of_the_same_size_is_obliged_to_be_rejected()
        {
            var gun = Node<AdvComboNodeSo>("Combo");
            gun.ammo = 12.5f;
            gun.rpm = 777;
            Dirty(gun);
            Rebuild();

            var offset = OffsetOf(gun, "IAdvCombat");
            var db = Combat();
            try
            {
                Assert.Throws<InvalidOperationException>(() => { _ = db.Read<AdvGunTwin>(offset).Rpm; },
                    "an AdvGun lies at this address; handing it out as an AdvGunTwin is not allowed even at equal size");
            }
            finally
            {
                db.Dispose();
            }
        }

        [Test]
        public void A_mix_of_bool_enum_and_alignment_outlives_the_round_trip()
        {
            var node = Node<AdvMixedNodeSo>("Mixed");
            node.flag = true;
            node.tier = AdvTier.High;
            node.weight = -1234.5678;
            node.small = -31000;
            Dirty(node);
            Rebuild();

            var db = Combat();
            try
            {
                ref readonly var record = ref db.Read<AdvMixed>(OffsetOf(node, "IAdvCombat"));

                Assert.That(record.Flag, Is.True, "a bool outlives the round trip as it is, without turning into a random 0/1");
                Assert.That(record.Tier, Is.EqualTo(AdvTier.High));
                Assert.That(record.Weight, Is.EqualTo(-1234.5678).Within(0.0));
                Assert.That(record.Small, Is.EqualTo((short)-31000));
            }
            finally
            {
                db.Dispose();
            }
        }

        [Test]
        public void The_raw_path_and_the_typed_one_lay_down_the_same_bytes()
        {
            var typed = Node<AdvLooseNodeSo>("Typed");
            var raw = Node<AdvRawNodeSo>("Raw");
            typed.a = 0x0102030405060708L;
            typed.b = 0x1112131415161718L;
            raw.size = 16;
            raw.seed = 0;
            Dirty(typed);
            Dirty(raw);

            Rebuild();

            var file = Bytes("IAdvLoose");
            var typedAt = OffsetOf(typed, "IAdvLoose");
            var rawAt = OffsetOf(raw, "IAdvLoose");

            Assert.That(BitConverter.ToInt64(file, (int)typedAt), Is.EqualTo(typed.a),
                "a typed record is exactly the bytes of the struct, little-endian, with no wrappers");

            var db = Loose();
            try
            {
                Assert.That(db.Read<AdvLooseBlock>(typedAt).B, Is.EqualTo(typed.b));

                // A raw record of the same size is read the same way: it has no type, but the bytes are the same.
                Assert.That(file[(int)rawAt], Is.EqualTo((byte)0));
                Assert.That(file[(int)rawAt + 15], Is.EqualTo((byte)15));
            }
            finally
            {
                db.Dispose();
            }
        }
    }
}

using System;
using System.IO;
using System.Text;
using NUnit.Framework;

namespace Blobcheg.Tests
{
    /// <summary>
    /// The layout and the writer. The main property proven here: the traversal order does not affect
    /// the file, and editing a value does not move the offsets.
    /// </summary>
    public sealed class BlobchegWriterTests
    {
        string _dir;

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(), "blobcheg-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_dir))
                Directory.Delete(_dir, true);
        }

        static byte[] Payload(byte fill, int size = 8)
        {
            var bytes = new byte[size];
            for (var i = 0; i < size; i++)
                bytes[i] = fill;

            return bytes;
        }

        static BlobchegRecord Rec(string type, string key, byte fill, int size = 8)
            => new BlobchegRecord(type, key, 0, "node-" + key, Payload(fill, size));

        [Test]
        public void Records_are_grouped_by_type_and_aligned_to_16()
        {
            var writer = BlobchegWriter.Open(_dir, "Domain");
            var shield = writer.Append(Rec("Shield", "b", 2));
            var gunB = writer.Append(Rec("Gun", "b", 1));
            var gunA = writer.Append(Rec("Gun", "a", 3));
            writer.Flush();

            Assert.That(writer.OffsetOf(gunA), Is.EqualTo(BlobchegFormat.HeaderSize), "the Gun type comes first, and inside it the key 'a'");
            Assert.That(writer.OffsetOf(gunB), Is.GreaterThan(writer.OffsetOf(gunA)));
            Assert.That(writer.OffsetOf(shield), Is.GreaterThan(writer.OffsetOf(gunB)), "by FullName Shield comes after Gun");

            foreach (var offset in new[] { writer.OffsetOf(gunA), writer.OffsetOf(gunB), writer.OffsetOf(shield) })
                Assert.That(offset % BlobchegFormat.RecordAlign, Is.Zero, "the start of a record is aligned to 16");
        }

        [Test]
        public void The_traversal_order_does_not_affect_the_file()
        {
            var straight = BlobchegWriter.Open(_dir, "Straight");
            straight.Append(Rec("Gun", "a", 1));
            straight.Append(Rec("Gun", "b", 2));
            straight.Append(Rec("Shield", "a", 3));
            straight.Flush();

            var reversed = BlobchegWriter.Open(_dir, "Reversed");
            reversed.Append(Rec("Shield", "a", 3));
            reversed.Append(Rec("Gun", "b", 2));
            reversed.Append(Rec("Gun", "a", 1));
            reversed.Flush();

            Assert.That(reversed.ContentHash, Is.EqualTo(straight.ContentHash));
            CollectionAssert.AreEqual(
                Body(Path.Combine(_dir, "Straight.bcheg")),
                Body(Path.Combine(_dir, "Reversed.bcheg")));
        }

        [Test]
        public void Raw_records_land_in_the_tail()
        {
            var writer = BlobchegWriter.Open(_dir, "Domain");
            var raw = writer.Append(new BlobchegRecord(null, "a", 0, "raw", Payload(9, 5)));
            var typed = writer.Append(Rec("Zzz", "a", 1));
            writer.Flush();

            Assert.That(writer.OffsetOf(raw), Is.GreaterThan(writer.OffsetOf(typed)),
                "raw blocks of variable length must not drag the typed ones along with them");
        }

        [Test]
        public void Editing_a_value_does_not_move_the_offsets()
        {
            var before = BlobchegWriter.Open(_dir, "Domain");
            var a = before.Append(Rec("Gun", "a", 1));
            var b = before.Append(Rec("Gun", "b", 2));
            before.Flush();

            var after = BlobchegWriter.Open(_dir, "Domain");
            var a2 = after.Append(Rec("Gun", "a", 77));
            var b2 = after.Append(Rec("Gun", "b", 2));
            after.Flush();

            Assert.That(after.OffsetOf(a2), Is.EqualTo(before.OffsetOf(a)));
            Assert.That(after.OffsetOf(b2), Is.EqualTo(before.OffsetOf(b)));
            Assert.That(after.RevisionOf(a2), Is.Not.EqualTo(before.RevisionOf(a)), "the revision is obliged to notice the edit");
            Assert.That(after.RevisionOf(b2), Is.EqualTo(before.RevisionOf(b)), "an untouched node keeps the same revision");
        }

        [Test]
        public void Unchanged_content_does_not_rewrite_the_file()
        {
            var first = BlobchegWriter.Open(_dir, "Domain");
            first.Append(Rec("Gun", "a", 1));
            first.Flush();
            Assert.That(first.FileChanged, Is.True);

            var second = BlobchegWriter.Open(_dir, "Domain");
            second.Append(Rec("Gun", "a", 1));
            second.Flush();
            Assert.That(second.FileChanged, Is.False, "the same content means the file is not touched, otherwise everything gets rebaked");
        }

        [Test]
        public void A_claimed_address_stays_with_its_record_and_a_new_one_settles_into_the_tail()
        {
            var before = BlobchegWriter.Open(_dir, "Domain");
            var only = before.Append(Rec("Gun", "b", 1));
            before.Flush();
            var kept = before.OffsetOf(only);

            var after = BlobchegWriter.Open(_dir, "Domain");
            var newcomer = after.Append(Rec("Gun", "a", 5));
            var old = after.Append(Rec("Gun", "b", 1));
            after.Claim(old, kept);
            after.Flush();

            Assert.That(after.OffsetOf(old), Is.EqualTo(kept), "the previous address is obliged to stay with the previous record");
            Assert.That(after.OffsetOf(newcomer), Is.GreaterThan(kept),
                "the new record settles into the tail although by key it would come first — otherwise it shifts someone else's address");
        }

        [Test]
        public void A_deleted_record_leaves_a_hole_and_the_neighbours_do_not_move()
        {
            var before = BlobchegWriter.Open(_dir, "Domain");
            var a = before.Append(Rec("Gun", "a", 1));
            var b = before.Append(Rec("Gun", "b", 2));
            var c = before.Append(Rec("Gun", "c", 3));
            before.Flush();
            var keptA = before.OffsetOf(a);
            var keptC = before.OffsetOf(c);
            Assert.That(before.OffsetOf(b), Is.GreaterThan(keptA));

            var after = BlobchegWriter.Open(_dir, "Domain");
            var a2 = after.Append(Rec("Gun", "a", 1));
            var c2 = after.Append(Rec("Gun", "c", 3));
            after.Claim(a2, keptA);
            after.Claim(c2, keptC);
            after.Flush();

            Assert.That(after.OffsetOf(a2), Is.EqualTo(keptA));
            Assert.That(after.OffsetOf(c2), Is.EqualTo(keptC), "the hole left by a deleted record does not pull the next one in");
        }

        // The premise of this test turned around together with the rule. A record that grew used to stay
        // in place while the NEIGHBOUR lost its spot — in a base with arrays one grown array would evict
        // dozens of other people's nodes. Now the claim is lost by whoever stopped fitting up to
        // someone else's address: the record that moves is exactly the one that was edited, and only its
        // consumers get rebaked.
        [Test]
        public void A_grown_claim_gives_the_spot_to_the_neighbour_and_moves_away_itself()
        {
            var before = BlobchegWriter.Open(_dir, "Domain");
            var a = before.Append(new BlobchegRecord(null, "a", 0, "raw-a", Payload(1, 8)));
            var b = before.Append(new BlobchegRecord(null, "b", 0, "raw-b", Payload(2, 8)));
            before.Flush();
            var keptA = before.OffsetOf(a);
            var keptB = before.OffsetOf(b);

            // The record grew into someone else's claimed address: it loses the spot, not the neighbour.
            var after = BlobchegWriter.Open(_dir, "Domain");
            var a2 = after.Append(new BlobchegRecord(null, "a", 0, "raw-a", Payload(1, 40)));
            var b2 = after.Append(new BlobchegRecord(null, "b", 0, "raw-b", Payload(2, 8)));
            after.Claim(a2, keptA);
            after.Claim(b2, keptB);
            after.Flush();

            Assert.That(after.OffsetOf(b2), Is.EqualTo(keptB), "the neighbour does not move: nobody edited it");
            Assert.That(after.OffsetOf(a2), Is.GreaterThanOrEqualTo(keptB + 8),
                "the grown record loses its claim and moves away — there can be no overlap in the file");
        }

        [Test]
        public void A_grown_claim_stays_if_there_is_room_ahead()
        {
            var writer = BlobchegWriter.Open(_dir, "Domain");
            var a = writer.Append(new BlobchegRecord(null, "a", 0, "raw-a", Payload(1, 40)));
            var b = writer.Append(new BlobchegRecord(null, "b", 0, "raw-b", Payload(2, 8)));
            writer.Claim(a, BlobchegFormat.HeaderSize);
            writer.Claim(b, BlobchegFormat.HeaderSize + 64);
            writer.Flush();

            Assert.That(writer.OffsetOf(a), Is.EqualTo(BlobchegFormat.HeaderSize),
                "there are 64 bytes up to someone else's address, a 40-byte record fits — nobody moves");
            Assert.That(writer.OffsetOf(b), Is.EqualTo(BlobchegFormat.HeaderSize + 64));
        }

        [Test]
        public void A_shrunk_claim_keeps_its_address()
        {
            var before = BlobchegWriter.Open(_dir, "Domain");
            var a = before.Append(new BlobchegRecord(null, "a", 0, "raw-a", Payload(1, 40)));
            var b = before.Append(new BlobchegRecord(null, "b", 0, "raw-b", Payload(2, 8)));
            before.Flush();
            var keptA = before.OffsetOf(a);
            var keptB = before.OffsetOf(b);

            var after = BlobchegWriter.Open(_dir, "Domain");
            var a2 = after.Append(new BlobchegRecord(null, "a", 0, "raw-a", Payload(1, 8)));
            var b2 = after.Append(new BlobchegRecord(null, "b", 0, "raw-b", Payload(2, 8)));
            after.Claim(a2, keptA);
            after.Claim(b2, keptB);
            after.Flush();

            Assert.That(after.OffsetOf(a2), Is.EqualTo(keptA), "the shrunk record stays, the remainder lies as dead bytes");
            Assert.That(after.OffsetOf(b2), Is.EqualTo(keptB));
        }

        [Test]
        public void The_last_claim_grows_freely()
        {
            var before = BlobchegWriter.Open(_dir, "Domain");
            var a = before.Append(new BlobchegRecord(null, "a", 0, "raw-a", Payload(1, 8)));
            var b = before.Append(new BlobchegRecord(null, "b", 0, "raw-b", Payload(2, 8)));
            before.Flush();
            var keptA = before.OffsetOf(a);
            var keptB = before.OffsetOf(b);

            var after = BlobchegWriter.Open(_dir, "Domain");
            var a2 = after.Append(new BlobchegRecord(null, "a", 0, "raw-a", Payload(1, 8)));
            var b2 = after.Append(new BlobchegRecord(null, "b", 0, "raw-b", Payload(2, 4096)));
            after.Claim(a2, keptA);
            after.Claim(b2, keptB);
            after.Flush();

            Assert.That(after.OffsetOf(a2), Is.EqualTo(keptA));
            Assert.That(after.OffsetOf(b2), Is.EqualTo(keptB), "past the last claim there is only the tail — no boundary");
        }

        [Test]
        public void A_new_record_settles_into_the_hole_of_a_deleted_one()
        {
            var before = BlobchegWriter.Open(_dir, "Domain");
            var a = before.Append(Rec("Gun", "a", 1));
            var b = before.Append(Rec("Gun", "b", 2));
            var c = before.Append(Rec("Gun", "c", 3));
            before.Flush();
            var keptA = before.OffsetOf(a);
            var freed = before.OffsetOf(b);
            var keptC = before.OffsetOf(c);
            var lengthBefore = new FileInfo(Path.Combine(_dir, "Domain.bcheg")).Length;

            // Node b is deleted, and a new d of the same size asks for its place.
            var after = BlobchegWriter.Open(_dir, "Domain");
            var a2 = after.Append(Rec("Gun", "a", 1));
            var c2 = after.Append(Rec("Gun", "c", 3));
            var d = after.Append(Rec("Gun", "d", 4));
            after.Claim(a2, keptA);
            after.Claim(c2, keptC);
            after.Flush();

            Assert.That(after.OffsetOf(d), Is.EqualTo(freed), "the hole from a deleted record is reused");
            Assert.That(new FileInfo(Path.Combine(_dir, "Domain.bcheg")).Length, Is.EqualTo(lengthBefore),
                "the file does not grow: the new record landed in the hole and not in the tail");
        }

        [Test]
        public void Ten_length_edits_do_not_grow_the_file_linearly()
        {
            // The record swings between 8 and 40 bytes; a neighbour stands as a claim right behind it.
            // Without reusing the holes, every edit would leave an abandoned chunk and the file would
            // grow by the sum of all the intermediate versions.
            var writer = BlobchegWriter.Open(_dir, "Domain");
            var a = writer.Append(new BlobchegRecord(null, "a", 0, "raw-a", Payload(1, 8)));
            var b = writer.Append(new BlobchegRecord(null, "b", 0, "raw-b", Payload(2, 8)));
            writer.Flush();
            var offsetA = writer.OffsetOf(a);
            var offsetB = writer.OffsetOf(b);

            var lengths = new long[10];
            for (var edit = 0; edit < 10; edit++)
            {
                var size = edit % 2 == 0 ? 40 : 8;
                var next = BlobchegWriter.Open(_dir, "Domain");
                var a2 = next.Append(new BlobchegRecord(null, "a", 0, "raw-a", Payload(1, size)));
                var b2 = next.Append(new BlobchegRecord(null, "b", 0, "raw-b", Payload(2, 8)));
                next.Claim(a2, offsetA);
                next.Claim(b2, offsetB);
                next.Flush();

                offsetA = next.OffsetOf(a2);
                offsetB = next.OffsetOf(b2);
                lengths[edit] = new FileInfo(Path.Combine(_dir, "Domain.bcheg")).Length;
            }

            for (var i = 4; i < lengths.Length; i++)
                Assert.That(lengths[i], Is.EqualTo(lengths[i - 2]),
                    "the layout is obliged to settle into a stable cycle rather than grow with every edit");
        }

        [Test]
        public void The_traversal_order_does_not_affect_a_file_with_holes()
        {
            // A hole and a new record settling into it must not break the determinism of the layout.
            var straightBefore = BlobchegWriter.Open(_dir, "Straight");
            var sa = straightBefore.Append(Rec("Gun", "a", 1));
            var sb = straightBefore.Append(Rec("Gun", "b", 2));
            var sc = straightBefore.Append(Rec("Gun", "c", 3));
            straightBefore.Flush();
            var keptA = straightBefore.OffsetOf(sa);
            var keptC = straightBefore.OffsetOf(sc);

            var straight = BlobchegWriter.Open(_dir, "Straight");
            var s1 = straight.Append(Rec("Gun", "a", 1));
            var s2 = straight.Append(Rec("Gun", "c", 3));
            var s3 = straight.Append(Rec("Gun", "d", 4));
            straight.Claim(s1, keptA);
            straight.Claim(s2, keptC);
            straight.Flush();

            var reversed = BlobchegWriter.Open(_dir, "Reversed");
            var r3 = reversed.Append(Rec("Gun", "d", 4));
            var r2 = reversed.Append(Rec("Gun", "c", 3));
            var r1 = reversed.Append(Rec("Gun", "a", 1));
            reversed.Claim(r1, keptA);
            reversed.Claim(r2, keptC);
            reversed.Flush();

            CollectionAssert.AreEqual(
                Body(Path.Combine(_dir, "Straight.bcheg")),
                Body(Path.Combine(_dir, "Reversed.bcheg")));
        }

        [Test]
        public void Without_claims_there_are_no_holes()
        {
            // A first build and a compaction: there are no claims, the records lie back to back with
            // alignment — exactly the layout that always was.
            var writer = BlobchegWriter.Open(_dir, "Domain");
            var a = writer.Append(Rec("Gun", "a", 1));
            var b = writer.Append(Rec("Gun", "b", 2));
            var c = writer.Append(Rec("Gun", "c", 3));
            writer.Flush();

            Assert.That(writer.OffsetOf(a), Is.EqualTo(BlobchegFormat.HeaderSize));
            Assert.That(writer.OffsetOf(b), Is.EqualTo(BlobchegFormat.AlignUp(writer.OffsetOf(a) + 8)));
            Assert.That(writer.OffsetOf(c), Is.EqualTo(BlobchegFormat.AlignUp(writer.OffsetOf(b) + 8)));
        }

        [Test]
        public void A_garbage_claim_does_not_break_the_layout()
        {
            var plain = BlobchegWriter.Open(_dir, "Plain");
            var expected = plain.Append(Rec("Gun", "a", 1));
            plain.Flush();

            var claimed = BlobchegWriter.Open(_dir, "Claimed");
            var ticket = claimed.Append(Rec("Gun", "a", 1));
            claimed.Claim(ticket, 7);
            claimed.Flush();

            Assert.That(claimed.OffsetOf(ticket), Is.EqualTo(plain.OffsetOf(expected)),
                "an address off the alignment is not an address; the record gets a place like a new one");
        }

        [Test]
        public void Claim_after_Flush_throws()
        {
            var writer = BlobchegWriter.Open(_dir, "Domain");
            var ticket = writer.Append(Rec("Gun", "a", 1));
            writer.Flush();
            Assert.Throws<InvalidOperationException>(() => writer.Claim(ticket, BlobchegFormat.HeaderSize));
        }

        [Test]
        public void Two_records_from_one_node_into_a_domain_throw()
        {
            var writer = BlobchegWriter.Open(_dir, "Domain");
            writer.Append(Rec("Gun", "a", 1));
            Assert.Throws<InvalidOperationException>(() => writer.Append(Rec("Gun", "a", 2)));
        }

        [Test]
        public void An_offset_before_Flush_throws()
        {
            var writer = BlobchegWriter.Open(_dir, "Domain");
            var ticket = writer.Append(Rec("Gun", "a", 1));
            Assert.Throws<InvalidOperationException>(() => writer.OffsetOf(ticket));
            Assert.Throws<InvalidOperationException>(() => writer.RevisionOf(ticket));
        }

        [Test]
        public void Append_after_Flush_throws()
        {
            var writer = BlobchegWriter.Open(_dir, "Domain");
            writer.Append(Rec("Gun", "a", 1));
            writer.Flush();
            Assert.Throws<InvalidOperationException>(() => writer.Append(Rec("Gun", "b", 2)));
        }

        [Test]
        public void An_empty_domain_gives_a_file_of_one_header()
        {
            var writer = BlobchegWriter.Open(_dir, "Empty");
            writer.Flush();

            var file = File.ReadAllBytes(Path.Combine(_dir, "Empty.bcheg"));
            Assert.That(file.Length, Is.EqualTo(BlobchegFormat.HeaderSize));
        }

        [Test]
        public void The_file_name_is_assembled_from_the_domain_name()
        {
            Assert.That(BlobchegNaming.FileName("IHotPathCombatData"), Is.EqualTo("IHotPathCombatData.bcheg"));
            Assert.Throws<ArgumentException>(() => BlobchegNaming.FileName(""));
        }

        static byte[] Body(string path)
        {
            var file = File.ReadAllBytes(path);
            var body = new byte[file.Length - BlobchegFormat.HeaderSize];
            Buffer.BlockCopy(file, BlobchegFormat.HeaderSize, body, 0, body.Length);
            return body;
        }

        [Test]
        public void The_debug_section_carries_the_type_and_node_names()
        {
            var writer = BlobchegWriter.Open(_dir, "Domain");
            writer.Append(new BlobchegRecord("Ns.Gun", "a", 0xDEAD, "SuperGun", Payload(1)));
            writer.Flush(withDebug: true);

            var file = File.ReadAllBytes(Path.Combine(_dir, "Domain.bcheg"));
            var debugOffset = BitConverter.ToUInt32(file, 12);
            Assert.That(debugOffset, Is.Not.Zero);
            Assert.That(BitConverter.ToUInt32(file, (int)debugOffset), Is.EqualTo(BlobchegDebugSection.Magic));

            var count = BitConverter.ToUInt32(file, (int)debugOffset + 4);
            Assert.That(count, Is.EqualTo(1));

            var typeHash = BitConverter.ToUInt32(file, (int)debugOffset + BlobchegDebugSection.PrologSize + 8);
            Assert.That(typeHash, Is.EqualTo(0xDEAD));

            var nameOffset = BitConverter.ToUInt32(file, (int)debugOffset + BlobchegDebugSection.PrologSize + 12);
            var typeLength = BitConverter.ToUInt16(file, (int)nameOffset);
            Assert.That(Encoding.UTF8.GetString(file, (int)nameOffset + 2, typeLength), Is.EqualTo("Ns.Gun"));
        }

        [Test]
        public void Without_the_define_there_is_no_section_in_the_file()
        {
            var writer = BlobchegWriter.Open(_dir, "Domain");
            writer.Append(Rec("Gun", "a", 1));
            writer.Flush();

            var file = File.ReadAllBytes(Path.Combine(_dir, "Domain.bcheg"));
            Assert.That(BitConverter.ToUInt32(file, 12), Is.Zero, "debugOffset");
            Assert.That(BitConverter.ToUInt16(file, 6), Is.Zero, "flags");
        }
    }
}

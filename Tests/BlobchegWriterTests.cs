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
        public void A_deleted_record_leaves_no_hole()
        {
            var before = BlobchegWriter.Open(_dir, "Domain");
            var a = before.Append(Rec("Gun", "a", 1));
            var b = before.Append(Rec("Gun", "b", 2));
            before.Flush();

            Assert.That(before.OffsetOf(b), Is.GreaterThan(before.OffsetOf(a)));

            var after = BlobchegWriter.Open(_dir, "Domain");
            var kept = after.Append(Rec("Gun", "b", 2));
            after.Flush();

            Assert.That(after.OffsetOf(kept), Is.EqualTo(before.OffsetOf(a)),
                "the survivor moves into the place of the departed: the layout is a function of what is there now");
        }

        [Test]
        public void A_newcomer_ahead_by_key_moves_its_neighbours()
        {
            var before = BlobchegWriter.Open(_dir, "Domain");
            var only = before.Append(Rec("Gun", "b", 1));
            before.Flush();

            var after = BlobchegWriter.Open(_dir, "Domain");
            var newcomer = after.Append(Rec("Gun", "a", 5));
            var old = after.Append(Rec("Gun", "b", 1));
            after.Flush();

            Assert.That(after.OffsetOf(newcomer), Is.EqualTo(before.OffsetOf(only)),
                "the key decides the place, and nothing else does");

            Assert.That(after.OffsetOf(old), Is.GreaterThan(before.OffsetOf(only)),
                "that is the price of a journal-free layout: the neighbour moves and everything baked against it is baked again");
        }

        [Test]
        public void The_records_lie_back_to_back_with_alignment()
        {
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

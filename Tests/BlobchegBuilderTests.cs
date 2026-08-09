using System;
using System.IO;
using Blobcheg.Authoring;
using NUnit.Framework;
using Unity.Collections;

namespace Blobcheg.Tests
{
    /// <summary>
    /// The record builder on its own: the layout of the tail, the self-relative offsets, the limits. The
    /// full circle — the bytes assembled by the builder are loaded by a base and read through a
    /// <see cref="BlobchegArray{T}"/>.
    /// </summary>
    public sealed class BlobchegBuilderTests
    {
        string _dir;

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(), "blobcheg-builder-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_dir))
                Directory.Delete(_dir, true);
        }

        static BlobchegBuilder<T> Open<T>(Action<byte[]> sink) where T : unmanaged
            => new BlobchegBuilder<T>("node", sink);

        [Test]
        public void The_offset_is_measured_from_the_field_address()
        {
            byte[] built = null;
            var b = Open<TestCurve>(bytes => built = bytes);
            b.Root.Levels = 7;
            var values = b.Allocate(ref b.Root.Values, 3);
            values[0] = 1.5f;
            values[1] = 2.5f;
            values[2] = 3.5f;
            b.End();

            // The head is 12 bytes, the float tail starts at 12; the array field sits at 4 → offset 8.
            Assert.That(built.Length, Is.EqualTo(24));
            Assert.That(BitConverter.ToInt32(built, 0), Is.EqualTo(7));
            Assert.That(BitConverter.ToInt32(built, 4), Is.EqualTo(8));
            Assert.That(BitConverter.ToInt32(built, 8), Is.EqualTo(3));
            Assert.That(BitConverter.ToSingle(built, 12), Is.EqualTo(1.5f));
            Assert.That(BitConverter.ToSingle(built, 20), Is.EqualTo(3.5f));
        }

        [Test]
        public void An_empty_array_leaves_the_field_zero_and_does_not_grow_the_record()
        {
            byte[] built = null;
            var b = Open<TestCurve>(bytes => built = bytes);
            b.Allocate(ref b.Root.Values, 0);
            b.End();

            Assert.That(built.Length, Is.EqualTo(12), "there is no chunk for emptiness");
            Assert.That(BitConverter.ToInt32(built, 4), Is.Zero);
            Assert.That(BitConverter.ToInt32(built, 8), Is.Zero);
        }

        [Test]
        public void A_record_assembled_by_the_builder_is_read_by_a_base()
        {
            byte[] built = null;
            var b = Open<TestTable>(bytes => built = bytes);
            var rows = b.Allocate(ref b.Root.Rows, 2);
            var first = b.Allocate(ref rows[0].Cells, 2);
            first[0] = 10;
            first[1] = 20;
            var second = b.Allocate(ref rows[1].Cells, 1);
            second[0] = 30;
            b.End();

            var writer = BlobchegWriter.Open(_dir, "BuilderDomain");
            var ticket = writer.Append(new BlobchegRecord(typeof(TestTable).FullName, "a", 0, "node", built));
            writer.Flush();

            var file = File.ReadAllBytes(Path.Combine(_dir, "BuilderDomain.bcheg"));
            var blob = new BlobchegBlob(BlobchegBuffer.From(file, Allocator.Temp), "BuilderDomain");
            try
            {
                ref readonly var table = ref blob.Read<TestTable>(writer.OffsetOf(ticket));
                Assert.That(table.Rows.Length, Is.EqualTo(2));
                Assert.That(table.Rows[0].Cells[0], Is.EqualTo(10));
                Assert.That(table.Rows[0].Cells[1], Is.EqualTo(20));
                Assert.That(table.Rows[1].Cells[0], Is.EqualTo(30));
            }
            finally
            {
                blob.Dispose();
            }
        }

        [Test]
        public void An_array_window_survives_a_neighbouring_Allocate()
        {
            byte[] built = null;
            var b = Open<TestTable>(bytes => built = bytes);
            var rows = b.Allocate(ref b.Root.Rows, 1);
            b.Allocate(ref rows[0].Cells, 1)[0] = 5;

            // The chunks do not move: the rows window is obliged to stay alive after a neighbour's Allocate.
            Assert.That(rows.Length, Is.EqualTo(1));
            b.End();
            Assert.That(built, Is.Not.Null);
        }

        [Test]
        public void A_negative_length_throws()
        {
            var b = Open<TestCurve>(_ => { });
            Assert.Throws<ArgumentOutOfRangeException>(() => b.Allocate(ref b.Root.Values, -1));
            b.Abandon();
        }

        [Test]
        public void A_foreign_field_throws()
        {
            var b = Open<TestCurve>(_ => { });
            var foreign = new TestCurve();
            var thrown = Assert.Throws<InvalidOperationException>(() => b.Allocate(ref foreign.Values, 1));
            StringAssert.Contains("not from this record", thrown.Message);
            b.Abandon();
        }

        [Test]
        public void A_repeated_Allocate_into_the_same_field_throws()
        {
            var b = Open<TestCurve>(_ => { });
            b.Allocate(ref b.Root.Values, 2);
            var thrown = Assert.Throws<InvalidOperationException>(() => b.Allocate(ref b.Root.Values, 3));
            StringAssert.Contains("Values", thrown.Message, "the error is obliged to name the field");
            b.Abandon();
        }

        [Test]
        public void Working_after_End_throws()
        {
            byte[] built = null;
            var b = Open<TestCurve>(bytes => built = bytes);
            b.End();

            var foreign = new TestCurve();
            Assert.Throws<InvalidOperationException>(() => _ = b.Root.Levels);
            Assert.Throws<InvalidOperationException>(() => b.Allocate(ref foreign.Values, 1));
            Assert.Throws<InvalidOperationException>(() => b.End());
            Assert.That(built, Is.Not.Null, "the first End did assemble the record");
        }

        [Test]
        public void An_index_past_the_window_bounds_throws()
        {
            var b = Open<TestCurve>(_ => { });
            var values = b.Allocate(ref b.Root.Values, 2);

            // A ref struct cannot be captured into a lambda — we catch it by hand.
            var caught = false;
            try
            {
                values[2] = 1f;
            }
            catch (IndexOutOfRangeException)
            {
                caught = true;
            }

            Assert.That(caught, Is.True);
            b.Abandon();
        }
    }
}
